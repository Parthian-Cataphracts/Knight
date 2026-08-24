import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useMutation } from "@tanstack/react-query";
import { Shield, Mail, Lock, ArrowLeft, ShieldCheck, KeyRound, Copy } from "lucide-react";
import { apiRequest, setAccessToken } from "@/lib/api/client";
import { ApiError } from "@/lib/api/problem";
import type { LoginRequest, LoginResponse, MfaEnrollmentResponse } from "@/lib/api/types";
import { digitsOnly } from "@/lib/utils/format";
import { useAuthStore } from "@/store/auth";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";

/**
 * Sign-in, driven by the status the API returns rather than by guessing from the
 * user object (docs/authentication.md section 1):
 *
 *   succeeded                → signed in
 *   mfa_required             → the account has a second factor; ask for the code
 *                              and re-post the credentials with it
 *   mfa_enrollment_required  → the account holds a role that requires a second
 *                              factor but has none yet. It already has a session,
 *                              which can reach enrolment and nothing else, so the
 *                              only way forward is to enrol here.
 */
type Step = "credentials" | "mfa" | "enrol";

export function LoginPage() {
  const { t } = useTranslation();
  const signIn = useAuthStore((state) => state.signIn);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [step, setStep] = useState<Step>("credentials");
  const [enrollment, setEnrollment] = useState<MfaEnrollmentResponse | null>(null);

  const complete = (response: LoginResponse) => {
    if (response.accessToken && response.user) {
      signIn(response.user, response.accessToken);
    }
  };

  const login = useMutation({
    mutationFn: (credentials: LoginRequest) =>
      apiRequest<LoginResponse>("/auth/login", { method: "POST", body: credentials }),
    onSuccess: async (response) => {
      if (response.status === "mfa_required") {
        setCode("");
        setStep("mfa");
        return;
      }

      if (response.status === "mfa_enrollment_required") {
        // The session that comes back can reach enrolment only; the client has
        // to carry it for the next two calls.
        setAccessToken(response.accessToken);
        setCode("");
        setStep("enrol");
        setEnrollment(await apiRequest<MfaEnrollmentResponse>("/auth/mfa/enroll", { method: "POST" }));
        return;
      }

      complete(response);
    },
  });

  const confirmMfa = useMutation({
    mutationFn: (value: string) =>
      apiRequest<LoginResponse>("/auth/mfa/confirm", { method: "POST", body: { code: value } }),
    onSuccess: complete,
  });

  const pending = login.isPending || confirmMfa.isPending;

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();

    if (step === "enrol") {
      confirmMfa.mutate(code);
      return;
    }

    // The second leg of an MFA login re-posts the same credentials with the
    // code; the API has no separate verify endpoint by design.
    login.mutate(step === "mfa" ? { email, password, mfaCode: code } : { email, password });
  };

  const failure = login.error ?? confirmMfa.error;
  const error = failure instanceof ApiError ? failure : null;
  const fieldError = (field: string) => error?.validationErrors?.[field]?.[0];

  const backToCredentials = () => {
    setAccessToken(null);
    setEnrollment(null);
    setCode("");
    setStep("credentials");
  };

  return (
    <div className="grid min-h-dvh place-items-center bg-surface px-4 py-10">
      <div className="w-full max-w-md">
        <div className="mb-8 flex flex-col items-center gap-3 text-center">
          <span className="grid size-14 place-items-center rounded-lg bg-primary/15 text-primary">
            <Shield className="size-7" aria-hidden />
          </span>
          <h1 className="text-headline font-semibold text-on-surface">{t("auth.title")}</h1>
          <p className="text-body-sm text-on-surface-variant">{t("auth.subtitle")}</p>
        </div>

        <form onSubmit={onSubmit} className="card-surface elevated flex flex-col gap-5 p-6">
          {step === "credentials" ? (
            <>
              <TextField
                label={t("auth.email")}
                type="email"
                autoComplete="username"
                dir="ltr"
                required
                value={email}
                onChange={(event) => setEmail(event.target.value)}
                icon={<Mail className="size-4" aria-hidden />}
                error={fieldError("email")}
              />
              <TextField
                label={t("auth.password")}
                type="password"
                autoComplete="current-password"
                dir="ltr"
                required
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                icon={<Lock className="size-4" aria-hidden />}
                error={fieldError("password")}
              />

              <div className="flex flex-wrap items-center justify-between gap-3">
                <label className="flex items-center gap-2 text-body-sm text-on-surface-variant">
                  <input
                    type="checkbox"
                    className="size-4 rounded-sm border-outline-variant bg-surface-low accent-[var(--primary)]"
                  />
                  {t("auth.remember")}
                </label>
                <a href="#reset" className="text-body-sm text-primary hover:underline">
                  {t("auth.forgot")}
                </a>
              </div>
            </>
          ) : (
            <>
              {step === "enrol" ? (
                <div className="flex flex-col gap-3">
                  <p className="flex items-start gap-2 rounded-md bg-primary/10 px-3 py-2.5 text-body-sm text-primary">
                    <KeyRound className="mt-0.5 size-4 shrink-0" aria-hidden />
                    {t("auth.mfaEnrolPrompt")}
                  </p>
                  {enrollment ? (
                    <div className="flex items-center justify-between gap-2 rounded-md bg-surface-low px-3 py-2">
                      <code dir="ltr" className="break-all text-body-sm text-on-surface">
                        {enrollment.secret}
                      </code>
                      <button
                        type="button"
                        aria-label={t("auth.copySecret")}
                        onClick={() => void navigator.clipboard?.writeText(enrollment.secret)}
                        className="shrink-0 rounded-sm p-1 text-on-surface-variant hover:text-on-surface"
                      >
                        <Copy className="size-4" aria-hidden />
                      </button>
                    </div>
                  ) : null}
                </div>
              ) : (
                <p className="flex items-start gap-2 rounded-md bg-primary/10 px-3 py-2.5 text-body-sm text-primary">
                  <KeyRound className="mt-0.5 size-4 shrink-0" aria-hidden />
                  {t("auth.mfaPrompt")}
                </p>
              )}

              <TextField
                label={t("auth.mfaCode")}
                inputMode="numeric"
                autoComplete="one-time-code"
                dir="ltr"
                required
                maxLength={6}
                value={code}
                onChange={(event) => setCode(digitsOnly(event.target.value))}
                icon={<ShieldCheck className="size-4" aria-hidden />}
              />
            </>
          )}

          {error && !error.validationErrors ? (
            <p role="alert" className="rounded-md bg-error/15 px-3 py-2 text-body-sm text-error">
              {t("auth.failed")} — {error.message}
            </p>
          ) : null}

          <Button type="submit" loading={pending} disabled={step !== "credentials" && code.length < 6}>
            {step === "credentials" ? t("auth.submit") : t("auth.verify")}
            <ArrowLeft className="size-4 rtl:-scale-x-100" aria-hidden />
          </Button>

          {step === "credentials" ? null : (
            <button
              type="button"
              onClick={backToCredentials}
              className="text-body-sm text-on-surface-variant hover:text-on-surface"
            >
              {t("auth.backToCredentials")}
            </button>
          )}

          <p className="flex items-center justify-center gap-2 text-body-sm text-on-surface-variant">
            <ShieldCheck className="size-4 text-success" aria-hidden />
            {t("auth.secure")}
          </p>
        </form>
      </div>
    </div>
  );
}
