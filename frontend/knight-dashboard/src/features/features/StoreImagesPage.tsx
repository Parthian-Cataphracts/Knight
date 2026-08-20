import { useState, type ChangeEvent } from "react";
import { useTranslation } from "react-i18next";
import { useMutation } from "@tanstack/react-query";
import { Boxes, Upload } from "lucide-react";
import { useAction, useCollection } from "@/lib/api/hooks";
import { uploadArtifact } from "@/lib/api/client";
import type { ArtifactUpload, StoreImage } from "@/lib/api/domain";
import { PageShell, PageHeader, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { useAuthStore } from "@/store/auth";
import { formatDateTime } from "@/lib/utils/format";

const imageTone: Record<StoreImage["status"], Tone> = {
  Draft: "neutral",
  Published: "success",
  Yanked: "danger",
};

/**
 * The base store image registry: the versioned, signed images new stores are
 * built from (docs/store-provisioning.md §3).
 *
 * The publish form takes an **already-signed** package. Signing happens offline
 * in `knight_package.py`, so no signing key is ever present in the dashboard or
 * the API — this screen uploads the file, reads back the digest KNIGHT computed
 * from the stored bytes, and submits that digest with the detached signature.
 * The digest field is filled in by the upload rather than typed, because a
 * digest asserted by whoever supplied the file proves nothing about it.
 */
export function StoreImagesPage() {
  const { t } = useTranslation();
  const can = useAuthStore((state) => state.can);

  const images = useCollection<StoreImage>("/store-images");

  const [version, setVersion] = useState("");
  const [storeVersion, setStoreVersion] = useState("");
  const [signature, setSignature] = useState("");
  const [notes, setNotes] = useState("");
  const [uploaded, setUploaded] = useState<ArtifactUpload | null>(null);

  const upload = useMutation({
    mutationFn: (file: File) => uploadArtifact(file),
    onSuccess: setUploaded,
  });

  const publish = useAction<StoreImage, void>(
    () => ({
      path: "/store-images",
      options: {
        body: {
          version: version.trim(),
          storeVersion: storeVersion.trim(),
          packageReference: uploaded?.packageReference,
          artifactDigest: uploaded?.digest,
          signature: signature.trim(),
          releaseNotes: notes.trim() === "" ? null : notes.trim(),
        },
      },
    }),
    ["/store-images"],
  );

  const makeUsable = useAction<StoreImage, string>(
    (imageId) => ({ path: `/store-images/${imageId}/publish` }),
    ["/store-images"],
  );

  const yank = useAction<StoreImage, string>(
    (imageId) => ({
      path: `/store-images/${imageId}/yank`,
      options: { body: { reason: t("storeImages.yankReason") } },
    }),
    ["/store-images"],
  );

  const onFile = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (file) upload.mutate(file);
  };

  const columns: Column<StoreImage>[] = [
    { key: "version", header: t("storeImages.version"), mono: true, render: (row) => row.version },
    { key: "storeVersion", header: t("storeImages.storeVersion"), mono: true, render: (row) => row.storeVersion },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => <StatusChip tone={imageTone[row.status]}>{t(`storeImageStatus.${row.status}`)}</StatusChip>,
    },
    { key: "digest", header: t("storeImages.digest"), mono: true, secondary: true, render: (row) => row.artifactDigest.slice(0, 16) },
    { key: "key", header: t("storeImages.signingKey"), mono: true, secondary: true, render: (row) => row.signingKeyId },
    { key: "created", header: t("storeImages.created"), render: (row) => formatDateTime(row.createdAt) },
    {
      key: "actions",
      header: "",
      render: (row) =>
        can("feature.publish") && row.status === "Draft" ? (
          <Button size="sm" variant="outline" onClick={() => makeUsable.mutate(row.id)}>
            {t("storeImages.publish")}
          </Button>
        ) : can("feature.yank") && row.status === "Published" ? (
          <Button size="sm" variant="outline" onClick={() => yank.mutate(row.id)}>
            {t("storeImages.yank")}
          </Button>
        ) : null,
    },
  ];

  return (
    <PageShell>
      <PageHeader title={t("storeImages.title")} subtitle={t("storeImages.subtitle")} />

      {can("feature.publish") ? (
        <Card>
          <CardHeader title={t("storeImages.register")} icon={<Boxes className="size-5" />} />
          <CardBody className="flex flex-col gap-4">
            <p className="text-body-sm text-on-surface-variant">{t("storeImages.signingNote")}</p>

            <div className="grid gap-4 md:grid-cols-2">
              <TextField
                label={t("storeImages.version")}
                value={version}
                dir="ltr"
                placeholder="2.3.0"
                onChange={(event) => setVersion(event.target.value)}
              />
              <TextField
                label={t("storeImages.storeVersion")}
                value={storeVersion}
                dir="ltr"
                placeholder="2.3.0"
                onChange={(event) => setStoreVersion(event.target.value)}
              />
            </div>

            <label className="flex w-fit cursor-pointer items-center gap-2 rounded-md border border-outline-variant px-3 py-2 text-body-sm text-on-surface hover:bg-surface-high">
              <Upload className="size-4" aria-hidden />
              {upload.isPending ? t("storeImages.uploading") : t("storeImages.choosePackage")}
              <input type="file" className="hidden" accept=".zip,.whl,.gz" onChange={onFile} />
            </label>

            {uploaded ? (
              <p className="text-body-sm text-on-surface-variant">
                {t("storeImages.uploaded")} <Mono>{uploaded.digest.slice(0, 24)}…</Mono>{" "}
                ({Math.round(uploaded.sizeBytes / 1024)} KB)
              </p>
            ) : null}

            {upload.isError ? <p className="text-body-sm text-error">{upload.error.message}</p> : null}

            <TextField
              label={t("storeImages.signature")}
              value={signature}
              dir="ltr"
              placeholder="base64"
              onChange={(event) => setSignature(event.target.value)}
            />

            <TextField
              label={t("storeImages.notes")}
              value={notes}
              onChange={(event) => setNotes(event.target.value)}
            />

            <div>
              <Button
                disabled={uploaded === null || publish.isPending}
                onClick={() => publish.mutate(undefined, { onSuccess: () => setUploaded(null) })}
              >
                {t("storeImages.register")}
              </Button>
            </div>

            {publish.isError ? <p className="text-body-sm text-error">{publish.error.message}</p> : null}
          </CardBody>
        </Card>
      ) : null}

      <CollectionCard query={images}>
        {(rows) => (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.id}
            cardTitle={(row) => row.version}
            emptyMessage={t("storeImages.none")}
          />
        )}
      </CollectionCard>
    </PageShell>
  );
}
