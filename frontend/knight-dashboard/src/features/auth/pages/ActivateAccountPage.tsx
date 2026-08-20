import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useMutation } from "@tanstack/react-query";
import { Shield, Lock, CheckCircle2 } from "lucide-react";
import { apiRequest } from "@/lib/api/client";
import { ApiError } from "@/lib/api/problem";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";

/**
 * Completes an invitation: the holder of the emailed link chooses their own
 * password.
 *
 * Deliberately outside the authenticated shell — whoever arrives here is not
 * signed in, and cannot be. It also issues no session on success: the account
 * signs in through the ordinary login afterwards, which is what proves the
 * password it just set is the one it thinks it set.
 *
 * The token is read from the URL and never displayed. Showing it would put a
 * one-time credential on a screen people take photographs of.
 */
export function ActivateAccountPage() {
  const { t } = useTranslation();
  const token = new URLSearchParams(window.location.search).get("token") ?? "";

  const [password, setPassword] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [mismatch, setMismatch] = useState<string | undefined>(undefined);

  const activate = useMutation({
    mutationFn: () => apiRequest<void>("/auth/activate", { method: "POST", body: { token, password } }),
  });

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();

    if (password !== confirmation) {
      // Checked here because the API sees one password and cannot know the
      // person typed a different one the second time.
      setMismatch(t("activate.mismatch"));
      return;
    }

    setMismatch(undefined);
    activate.mutate();
  };

  const failure = activate.error instanceof ApiError ? activate.error : null;

  return (
    <div className="grid min-h-dvh place-items-center bg-surface px-4 py-10">
      <div className="w-full max-w-md">
        <div className="mb-8 flex flex-col items-center gap-3 text-center">
          <span className="grid size-14 place-items-center rounded-lg bg-primary/15 text-primary">
            <Shield className="size-7" aria-hidden />
          </span>
          <h1 className="text-headline font-semibold text-on-surface">{t("activate.title")}</h1>
          <p className="text-body-sm text-on-surface-variant">{t("activate.subtitle")}</p>
        </div>

        {activate.isSuccess ? (
          <div className="card-surface elevated flex flex-col items-center gap-4 p-6 text-center">
            <CheckCircle2 className="size-8 text-success" aria-hidden />
            <p className="text-body text-on-surface">{t("activate.done")}</p>
            <Button onClick={() => window.location.assign("/")}>{t("activate.signIn")}</Button>
          </div>
        ) : token === "" ? (
          <div className="card-surface elevated p-6 text-center text-body-sm text-on-surface-variant">
            {t("activate.noToken")}
          </div>
        ) : (
          <form onSubmit={onSubmit} className="card-surface elevated flex flex-col gap-5 p-6">
            <TextField
              label={t("activate.password")}
              type="password"
              autoComplete="new-password"
              icon={<Lock className="size-4" aria-hidden />}
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              required
            />

            <TextField
              label={t("activate.confirm")}
              type="password"
              autoComplete="new-password"
              icon={<Lock className="size-4" aria-hidden />}
              value={confirmation}
              error={mismatch}
              onChange={(event) => setConfirmation(event.target.value)}
              required
            />

            {failure ? (
              <p className="text-body-sm text-error">{failure.message}</p>
            ) : null}

            <Button type="submit" loading={activate.isPending}>
              {t("activate.submit")}
            </Button>
          </form>
        )}
      </div>
    </div>
  );
}
