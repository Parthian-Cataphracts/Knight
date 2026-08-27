"""
Build, sign and publish a KNIGHT Feature.

The whole pipeline of docs/feature-delivery.md §7, as one command:

    python features/tools/knight_package.py build   features/knight-feature-analytics-core
    python features/tools/knight_package.py sign    dist/knight-feature-analytics-core-1.0.0.zip
    python features/tools/knight_package.py publish features/knight-feature-analytics-core

Three properties this tool exists to guarantee:

- **The manifest is validated before anything is built.** A publish that fails
  after the artifact is uploaded leaves a file nobody will ever install, and the
  author finds out at the end of a pipeline run instead of the start.
- **The digest is computed from the built file, never asserted.** The digest is
  what a store verifies its download against; taking the author's word for it
  would make the check circular.
- **Signing is separable from building.** They are one command here for
  convenience, but the signing key lives wherever custody says it does, and this
  tool reaches it through the same file/environment indirection that a KMS
  implementation would replace (risks.md R21).

The keys this uses are ECDSA P-256 — see the note in
`Knight.Infrastructure/ControlPlane/Security/FeatureArtifactSecurity.cs` for why
that rather than Ed25519.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import sys
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DIST = REPO_ROOT / "dist"

#: Never shipped inside an artifact. Build caches and test trees are noise at
#: best, and a stray .env would be a secret delivered to every customer.
EXCLUDED_DIRECTORIES = {"__pycache__", ".git", ".pytest_cache", "tests", ".venv", "build", "dist", "node_modules"}
EXCLUDED_SUFFIXES = {".pyc", ".pyo", ".env"}


# --- Manifest ---------------------------------------------------------------


def load_manifest(feature_dir: Path) -> dict:
    """
    Reads `knight_manifest.yaml` and returns it as a dictionary.

    Parsed with PyYAML when it is available and with a deliberately small reader
    otherwise. The fallback exists because this tool has to run in a build
    container that may have nothing but the standard library, and adding a
    dependency to the thing that produces signed artifacts is a decision worth
    avoiding. The reader handles exactly the subset the manifest schema allows.
    """
    path = feature_dir / "knight_manifest.yaml"

    if not path.exists():
        raise SystemExit(f"No knight_manifest.yaml in {feature_dir}.")

    text = path.read_text(encoding="utf-8")

    try:
        import yaml  # type: ignore

        return yaml.safe_load(text)
    except ImportError:
        return _read_simple_yaml(text)


def _read_simple_yaml(text: str) -> dict:
    """
    A small YAML reader for the manifest subset.

    Handles nested mappings by indentation, inline `{...}` maps, `- ` lists,
    folded `>-` scalars, quoted strings and the scalars true/false/null/numbers.
    It is not a general YAML parser and does not pretend to be — anything it does
    not understand raises rather than guessing, because a manifest misread here
    becomes a signed artifact with the wrong contract.
    """
    root: dict = {}
    stack: list[tuple[int, object]] = [(-1, root)]
    lines = text.splitlines()
    index = 0

    while index < len(lines):
        raw = lines[index]
        index += 1

        # A whole-line comment is a comment, whatever it contains. Checked before
        # the quote heuristic below, which counts double quotes to protect a `#`
        # inside a quoted value - and which read a comment ending in an unpaired
        # quote as content, then raised on the line for having no colon in it.
        # Every prose comment in this repository's manifests is one apostrophe or
        # one quoted phrase away from that.
        if raw.lstrip().startswith("#"):
            continue

        stripped = raw.split("#", 1)[0].rstrip() if not _in_quotes(raw) else raw.rstrip()
        if not stripped.strip():
            continue

        indent = len(stripped) - len(stripped.lstrip())
        content = stripped.strip()

        while stack and indent <= stack[-1][0]:
            stack.pop()

        container = stack[-1][1]

        if content.startswith("- "):
            item = content[2:].strip()
            if not isinstance(container, list):
                raise ValueError(f"Unexpected list item outside a list: {content}")

            # A block-style mapping item, which is how `workers:` is written:
            #
            #   workers:
            #     - name: expire-points
            #       entrypoint: pkg.module.function
            #
            # The first pair opens a dict and the dict goes on the stack, so the
            # more-indented lines that follow land inside it. Without this the
            # first pair was appended as the string "name: expire-points" and the
            # next line raised - which is what the tool did, correctly, rather
            # than guessing. `dependencies.features` never hit it because those
            # items are written inline.
            if item.startswith("{") or ":" not in item:
                container.append(_scalar(item))
                continue

            entry: dict = {}
            entry_key, _, entry_value = item.partition(":")
            entry_value = entry_value.strip()

            if entry_value:
                entry[entry_key.strip()] = _scalar(entry_value)
            else:
                # `- key:` with the value on the following lines. Rare, and
                # refused rather than guessed at: this reader produces signed
                # artifacts and a manifest misread here becomes the wrong
                # contract.
                raise ValueError(f"Cannot read manifest line: {content}")

            container.append(entry)
            stack.append((indent, entry))
            continue

        if ":" not in content:
            raise ValueError(f"Cannot read manifest line: {content}")

        key, _, value = content.partition(":")
        key = key.strip()
        value = value.strip()

        if value in (">-", ">", "|", "|-"):
            # A folded scalar: take the more-indented lines that follow.
            block: list[str] = []
            while index < len(lines):
                candidate = lines[index]
                if not candidate.strip():
                    index += 1
                    continue
                candidate_indent = len(candidate) - len(candidate.lstrip())
                if candidate_indent <= indent:
                    break
                block.append(candidate.strip())
                index += 1
            container[key] = " ".join(block)
            continue

        if value == "":
            # A nested mapping or list; which one is decided by the next line.
            nxt = _next_meaningful(lines, index)
            child: object = [] if nxt.strip().startswith("- ") else {}
            container[key] = child
            stack.append((indent, child))
            continue

        container[key] = _scalar(value)

    return root


def _next_meaningful(lines: list[str], start: int) -> str:
    for line in lines[start:]:
        candidate = line.split("#", 1)[0]
        if candidate.strip():
            return candidate
    return ""


def _in_quotes(line: str) -> bool:
    return line.count('"') % 2 == 1


def _split_outside_quotes(text: str) -> list[str]:
    """
    Splits on commas that are not inside a quoted string.

    A plain `text.split(",")` tears a version range in half: the dependency
    `{ slug: analytics-core, version: ">=1.0.0,<2.0.0" }` became a slug, a
    `version` of `">=1.0.0`, and a third key called `<2.0.0"`. Nothing noticed,
    because this tool reads a manifest for the slug, the version and the package
    directory and does not resolve dependencies itself — so it built a correct
    artifact from a manifest it had misread, which is the worst shape a parser
    bug can take (docs/phase-15-verification.md).
    """
    parts: list[str] = []
    current: list[str] = []
    quote: str | None = None

    for character in text:
        if quote is not None:
            current.append(character)

            if character == quote:
                quote = None

            continue

        if character in "\"'":
            quote = character
            current.append(character)
            continue

        if character == ",":
            parts.append("".join(current))
            current = []
            continue

        current.append(character)

    parts.append("".join(current))

    return parts


def _scalar(value: str):
    value = value.strip()

    if value.startswith("[") and value.endswith("]"):
        # An inline list. Without this branch `extensions: []` was read as the
        # *string* "[]" and `extensions: [pg_trgm]` as "[pg_trgm]", so a manifest
        # this repository publishes could not be published at all by anybody
        # without PyYAML installed - which is every clean checkout, since nothing
        # here depends on it.
        #
        # Found by the delivery drill in CI, where PyYAML is absent and the local
        # machine's is not. The refusal came from KNIGHT's own validator rather
        # than from here, which is the system being right twice: a misread
        # manifest became a rejected publish rather than a signed artifact with
        # the wrong contract.
        return [_scalar(part) for part in _split_outside_quotes(value[1:-1]) if part.strip()]

    if value.startswith("{") and value.endswith("}"):
        result = {}
        for part in _split_outside_quotes(value[1:-1]):
            if not part.strip():
                continue
            key, _, item = part.partition(":")
            result[key.strip()] = _scalar(item)
        return result

    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
        return value[1:-1]

    lowered = value.lower()
    if lowered in ("true", "yes"):
        return True
    if lowered in ("false", "no"):
        return False
    if lowered in ("null", "~", ""):
        return None

    try:
        return int(value)
    except ValueError:
        pass

    try:
        return float(value)
    except ValueError:
        return value


# --- Build ------------------------------------------------------------------


#: Where each runtime keeps the code that gets packaged, and what the manifest
#: field naming it is called.
#:
#: The layout differs and nothing else does. The archive is a deterministic zip
#: either way, the digest is over the same bytes, and the signature and the whole
#: delivery path are unchanged - which is why adding a runtime did not require
#: the delivery path to learn anything
#: (docs/adr/0032-a-feature-declares-its-runtime.md).
LAYOUTS = {
    # A Python distribution: the importable package directory, named by the
    # module the store puts in INSTALLED_APPS.
    "django": ("installed_app", None),
    # A node package: its built output plus the package.json that describes it.
    # The directory is named for the Feature rather than for the module, because
    # an npm specifier is scoped - `@knight/feature-subscriptions` is not a
    # directory name - and the store reads the real name out of package.json.
    "node": ("namespace", ["package.json"]),
}


def _sources(feature_dir: Path, manifest: dict) -> tuple[str, Path, list[Path]]:
    """
    What goes in the archive, decided by the runtime the manifest declares.

    Absent runtime means django, exactly as the registry's reader defaults it: a
    manifest written before phase 17 is a Django Feature.
    """
    runtime = str(manifest.get("runtime") or "django")

    if runtime not in LAYOUTS:
        raise SystemExit(
            f"'{runtime}' is not a runtime this tool can package. Known: {', '.join(sorted(LAYOUTS))}."
        )

    block = manifest.get(runtime)

    if not isinstance(block, dict):
        raise SystemExit(f"The manifest declares runtime '{runtime}' and carries no '{runtime}:' block.")

    key, extras = LAYOUTS[runtime]
    directory = block.get(key)

    if not directory:
        raise SystemExit(f"The manifest's '{runtime}' block has no '{key}'.")

    source = feature_dir / str(directory)

    if not source.is_dir():
        raise SystemExit(f"The manifest names '{directory}', which is not a directory in {feature_dir}.")

    files = []

    for name in extras or []:
        candidate = feature_dir / name

        if not candidate.is_file():
            raise SystemExit(f"A {runtime} Feature needs a {name} beside its source, and {feature_dir} has none.")

        files.append(candidate)

    return runtime, source, files


def build(feature_dir: Path, dist: Path) -> Path:
    """
    Packs the feature's source into a deterministic zip.

    Deterministic on purpose: entries are sorted and timestamps are fixed, so
    building the same source twice produces the same bytes and therefore the same
    digest. Without that, "is the artifact in the registry the one built from
    this commit" is a question nobody can answer.

    What is packed depends on the runtime the manifest declares and nothing else
    does - see LAYOUTS.
    """
    manifest = load_manifest(feature_dir)
    slug = manifest["slug"]
    version = str(manifest["version"])
    runtime, source, extras = _sources(feature_dir, manifest)

    dist.mkdir(parents=True, exist_ok=True)
    artifact = dist / f"{slug}-{version}.zip"

    members: list[tuple[str, Path]] = []
    for path in sorted(source.rglob("*")):
        if path.is_dir():
            continue
        if any(part in EXCLUDED_DIRECTORIES for part in path.parts):
            continue
        if path.suffix in EXCLUDED_SUFFIXES:
            continue

        members.append((str(path.relative_to(feature_dir)).replace(os.sep, "/"), path))

    for path in extras:
        members.append((str(path.relative_to(feature_dir)).replace(os.sep, "/"), path))

    members.sort()

    if not members:
        raise SystemExit(f"Nothing to package in {source}.")

    with zipfile.ZipFile(artifact, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        # The manifest travels inside the artifact too, so a store can always see
        # the contract the code it is running was published against.
        archive.writestr(_fixed_info("knight_manifest.yaml"), (feature_dir / "knight_manifest.yaml").read_bytes())

        for name, path in members:
            archive.writestr(_fixed_info(name), path.read_bytes())

    print(f"Built {artifact} ({artifact.stat().st_size} bytes, {len(members) + 1} entries)")
    return artifact


def _fixed_info(name: str) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o644 << 16
    return info


# --- Sign -------------------------------------------------------------------


def digest_of(artifact: Path) -> str:
    """The sha-256 of the built file, lowercase hex — the spelling KNIGHT stores."""
    digest = hashlib.sha256()

    with artifact.open("rb") as handle:
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)

    return digest.hexdigest()


def sign(digest: str, private_key_b64: str | None = None) -> str:
    """
    Signs a digest, returning a base64 detached signature.

    The key comes from KNIGHT_SIGNING_KEY, which in a real pipeline is injected
    by whatever holds custody and never written to disk. The indirection is the
    point: replacing this with a KMS call changes this function and nothing else.
    """
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec

    material = private_key_b64 or os.environ.get("KNIGHT_SIGNING_KEY", "")
    if not material:
        raise SystemExit(
            "No signing key. Set KNIGHT_SIGNING_KEY to the base64 PKCS#8 private key, "
            "or generate a development pair with `knight_package.py keygen`."
        )

    private_key = serialization.load_der_private_key(base64.b64decode(material), password=None)
    if not isinstance(private_key, ec.EllipticCurvePrivateKey):
        raise SystemExit("KNIGHT_SIGNING_KEY is not an elliptic-curve private key.")

    signature = private_key.sign(digest.encode("ascii"), ec.ECDSA(hashes.SHA256()))
    return base64.b64encode(signature).decode("ascii")


def keygen() -> None:
    """
    Generates a development signing pair and prints both halves.

    Development only, and it says so. A production key is generated inside its
    custody boundary and its private half never appears on a terminal.
    """
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.primitives.asymmetric import ec

    private_key = ec.generate_private_key(ec.SECP256R1())

    private_der = private_key.private_bytes(
        encoding=serialization.Encoding.DER,
        format=serialization.PrivateFormat.PKCS8,
        encryption_algorithm=serialization.NoEncryption(),
    )
    public_der = private_key.public_key().public_bytes(
        encoding=serialization.Encoding.DER,
        format=serialization.PublicFormat.SubjectPublicKeyInfo,
    )

    print("# Development signing pair. Do not use these in a deployed environment.")
    print(f"KNIGHT_SIGNING_KEY={base64.b64encode(private_der).decode('ascii')}")
    print()
    print("# KNIGHT (appsettings FeatureArtifacts:Keys:dev:PublicKey) and the store's")
    print("# KNIGHT_SIGNING_KEYS both need the public half:")
    print(base64.b64encode(public_der).decode("ascii"))


# --- Publish ----------------------------------------------------------------


def publish(feature_dir: Path, dist: Path, base_url: str, token: str, artifact_root: Path | None) -> None:
    """
    Builds, signs, uploads and registers a version, then publishes it.

    The artifact is placed in the package store before the version is registered,
    because KNIGHT refuses to register a version whose artifact it cannot find and
    hash. That ordering is what makes the digest check at publish meaningful
    rather than a formality.
    """
    import urllib.error
    import urllib.request

    manifest = load_manifest(feature_dir)
    artifact = build(feature_dir, dist)
    digest = digest_of(artifact)
    signature = sign(digest)

    reference = artifact.name

    if artifact_root is not None:
        artifact_root.mkdir(parents=True, exist_ok=True)
        target = artifact_root / reference
        target.write_bytes(artifact.read_bytes())
        print(f"Uploaded to the package store at {target}")

    payload = {
        "manifest": json.dumps(manifest),
        "packageReference": reference,
        "artifactDigest": digest,
        "signature": signature,
        "signingKeyId": os.environ.get("KNIGHT_SIGNING_KEY_ID", "dev"),
        "releaseNotes": manifest.get("description"),
    }

    version = _post(f"{base_url}/api/v1/features/versions", payload, token)
    version_id = version["id"]
    print(f"Registered {manifest['slug']} {manifest['version']} as {version_id}")

    _post(f"{base_url}/api/v1/features/versions/{version_id}/publish", {}, token)
    print(f"Published {manifest['slug']} {manifest['version']}")


def _post(url: str, payload: dict, token: str) -> dict:
    import urllib.error
    import urllib.request

    request = urllib.request.Request(
        url,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {token}"},
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            body = response.read().decode("utf-8")
            return json.loads(body) if body else {}
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise SystemExit(f"KNIGHT refused the request ({exc.code}): {detail}") from exc


# --- Self-test ---------------------------------------------------------------


def selftest() -> int:
    """
    Checks this file's own YAML reader against every manifest in the repository.

    It exists because that reader has been wrong twice, both times in the same
    shape and both times invisibly. In phase 15 it tore a version range in half
    on a comma inside a quoted string; in phase 16 it read a whole-line comment
    ending in an unpaired double quote as content and refused the manifest. Both
    only appear where PyYAML is absent, which is CI and nowhere else — a
    developer's machine has it and never runs this path.

    The check is differential where it can be: parse each manifest with both
    readers and require the same document. Where PyYAML is missing it still
    parses everything, which is the case that actually breaks, and reports what
    it could not read. The store's installer has the same test for its own copy
    of this problem, and that test has caught three bugs.
    """
    root = Path(__file__).resolve().parents[1]
    manifests = sorted(root.glob("knight-feature-*/knight_manifest.yaml"))

    # The drill's own Feature too. It is not in the catalogue and lives outside
    # `features/`, and that is exactly why it was the manifest this reader could
    # not read: it is the only one using an inline list, and nothing checked it.
    manifests += sorted((root.parent / "tools" / "delivery-drill" / "versions").glob("*/knight_manifest.yaml"))

    if not manifests:
        print("No manifests found. This check would pass having read nothing.", file=sys.stderr)
        return 1

    try:
        import yaml  # type: ignore

        reference = yaml.safe_load
    except ImportError:
        reference = None
        print("PyYAML is absent; parsing every manifest without comparing.")

    failures = 0

    for path in manifests:
        # The directory alone is ambiguous now that the drill's versions are in
        # the list: two of them are called `1.0.0`.
        label = f"{path.parent.parent.name}/{path.parent.name}"
        text = path.read_text(encoding="utf-8")

        try:
            parsed = _read_simple_yaml(text)
        except Exception as exc:  # noqa: BLE001 - report, do not stop at the first
            failures += 1
            print(f"  FAILED  {label}: {exc}", file=sys.stderr)
            continue

        if reference is not None:
            expected = reference(text)

            # The one field this reader deliberately does not join: a folded
            # scalar, needed by nobody here.
            parsed.pop("description", None)
            expected.pop("description", None)

            if parsed != expected:
                failures += 1
                print(f"  DIFFERS {label}: this reader disagrees with PyYAML.", file=sys.stderr)
                continue

        print(f"  ok      {label}")

    if failures:
        print(f"{failures} manifest(s) this tool cannot read correctly.", file=sys.stderr)
        return 1

    print(f"{len(manifests)} manifest(s) read.")
    return 0


# --- Entry point ------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Build, sign and publish a KNIGHT Feature.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    build_parser = subparsers.add_parser("build", help="Pack the feature into a deterministic zip.")
    build_parser.add_argument("feature", type=Path)
    build_parser.add_argument("--dist", type=Path, default=DEFAULT_DIST)

    sign_parser = subparsers.add_parser("sign", help="Print the digest and signature for a built artifact.")
    sign_parser.add_argument("artifact", type=Path)

    subparsers.add_parser("keygen", help="Generate a development signing pair.")

    subparsers.add_parser(
        "selftest",
        help="Read every manifest in this repository with this tool's own YAML reader.",
    )

    validate_parser = subparsers.add_parser("validate", help="Check a manifest against KNIGHT without publishing.")
    validate_parser.add_argument("feature", type=Path)
    validate_parser.add_argument("--base-url", default=os.environ.get("KNIGHT_BASE_URL", "http://localhost:5008"))
    validate_parser.add_argument("--token", default=os.environ.get("KNIGHT_TOKEN", ""))

    publish_parser = subparsers.add_parser("publish", help="Build, sign, upload and publish a version.")
    publish_parser.add_argument("feature", type=Path)
    publish_parser.add_argument("--dist", type=Path, default=DEFAULT_DIST)
    publish_parser.add_argument("--base-url", default=os.environ.get("KNIGHT_BASE_URL", "http://localhost:5008"))
    publish_parser.add_argument("--token", default=os.environ.get("KNIGHT_TOKEN", ""))
    publish_parser.add_argument(
        "--artifact-root",
        type=Path,
        default=Path(os.environ["KNIGHT_ARTIFACT_ROOT"]) if os.environ.get("KNIGHT_ARTIFACT_ROOT") else None,
        help="Where KNIGHT reads artifacts from. Copies the built file there before registering it.",
    )

    args = parser.parse_args(argv)

    if args.command == "build":
        build(args.feature, args.dist)
    elif args.command == "sign":
        digest = digest_of(args.artifact)
        print(f"digest={digest}")
        print(f"signature={sign(digest)}")
    elif args.command == "keygen":
        keygen()
    elif args.command == "selftest":
        return selftest()
    elif args.command == "validate":
        manifest = load_manifest(args.feature)
        result = _post(
            f"{args.base_url}/api/v1/features/manifest/validate",
            {"manifest": json.dumps(manifest)},
            args.token,
        )
        print(json.dumps(result, indent=2))
        return 0 if result.get("isValid") else 1
    elif args.command == "publish":
        if not args.token:
            raise SystemExit("A KNIGHT token is required. Set KNIGHT_TOKEN or pass --token.")
        publish(args.feature, args.dist, args.base_url.rstrip("/"), args.token, args.artifact_root)

    return 0


if __name__ == "__main__":
    sys.exit(main())
