import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { MailCheck, CheckCircle2, KeyRound, ArrowRight } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { useVerifyEmail } from "../api";

/**
 * Email verification. The token usually arrives in the link's query string and is
 * confirmed on load; a merchant who copied the code by hand can paste it. A bad
 * or expired token is just a bad token — never a hint about any account.
 */
export function PortalVerifyPage() {
  const { t } = useTranslation();
  const [params] = useSearchParams();
  const verify = useVerifyEmail();
  const [token, setToken] = useState(params.get("token") ?? "");
  const autoRan = useRef(false);

  useEffect(() => {
    const fromLink = params.get("token");
    if (fromLink && !autoRan.current) {
      autoRan.current = true;
      verify.mutate(fromLink);
    }
  }, [params, verify]);

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    verify.mutate(token.trim());
  };

  return (
    <div className="grid min-h-dvh place-items-center bg-surface px-4 py-10">
      <div className="w-full max-w-md">
        {verify.isSuccess ? (
          <div className="card-surface elevated flex flex-col items-center gap-4 p-8 text-center">
            <span className="grid size-14 place-items-center rounded-lg bg-success/15 text-success">
              <CheckCircle2 className="size-7" aria-hidden />
            </span>
            <h1 className="text-headline font-semibold text-on-surface">{t("portal.verify.doneTitle")}</h1>
            <p className="text-body-sm text-on-surface-variant">{t("portal.verify.doneBody")}</p>
            <Link
              to="/"
              className="inline-flex h-11 items-center justify-center gap-2 rounded-md bg-primary px-4 text-body-sm font-medium text-on-primary hover:opacity-90"
            >
              {t("portal.verify.signIn")}
              <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
            </Link>
          </div>
        ) : (
          <>
            <div className="mb-8 flex flex-col items-center gap-3 text-center">
              <span className="grid size-14 place-items-center rounded-lg bg-primary/15 text-primary">
                <MailCheck className="size-7" aria-hidden />
              </span>
              <h1 className="text-headline font-semibold text-on-surface">{t("portal.verify.title")}</h1>
              <p className="text-body-sm text-on-surface-variant">{t("portal.verify.subtitle")}</p>
            </div>

            <form onSubmit={onSubmit} className="card-surface elevated flex flex-col gap-5 p-6">
              <TextField
                label={t("portal.verify.token")}
                dir="ltr"
                required
                value={token}
                onChange={(e) => setToken(e.target.value)}
                icon={<KeyRound className="size-4" aria-hidden />}
              />

              {verify.isError ? (
                <p role="alert" className="rounded-md bg-error/15 px-3 py-2 text-body-sm text-error">
                  {t("portal.verify.failed")}
                </p>
              ) : null}

              <Button type="submit" loading={verify.isPending} disabled={token.trim().length === 0}>
                {t("portal.verify.submit")}
              </Button>

              <p className="text-center text-body-sm text-on-surface-variant">
                <Link to="/signup" className="text-primary hover:underline">
                  {t("portal.verify.backToSignup")}
                </Link>
              </p>
            </form>
          </>
        )}
      </div>
    </div>
  );
}
