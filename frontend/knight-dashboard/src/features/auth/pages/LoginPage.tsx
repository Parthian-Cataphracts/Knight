import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useMutation } from "@tanstack/react-query";
import { Shield, Mail, Lock, ArrowLeft, ShieldCheck } from "lucide-react";
import { apiRequest } from "@/lib/api/client";
import { ApiError } from "@/lib/api/problem";
import type { LoginRequest, LoginResponse } from "@/lib/api/types";
import { useAuthStore } from "@/store/auth";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";

export function LoginPage() {
  const { t } = useTranslation();
  const signIn = useAuthStore((state) => state.signIn);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");

  const login = useMutation({
    mutationFn: (credentials: LoginRequest) =>
      apiRequest<LoginResponse>("/auth/login", { method: "POST", body: credentials }),
    onSuccess: (response) => signIn(response.user, response.accessToken),
  });

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    login.mutate({ email, password });
  };

  const error = login.error instanceof ApiError ? login.error : null;
  const fieldError = (field: string) => error?.validationErrors?.[field]?.[0];

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

          {error && !error.validationErrors ? (
            <p role="alert" className="rounded-md bg-error/15 px-3 py-2 text-body-sm text-error">
              {t("auth.failed")} — {error.message}
            </p>
          ) : null}

          <Button type="submit" loading={login.isPending}>
            {t("auth.submit")}
            <ArrowLeft className="size-4 rtl:-scale-x-100" aria-hidden />
          </Button>

          <p className="flex items-center justify-center gap-2 text-body-sm text-on-surface-variant">
            <ShieldCheck className="size-4 text-success" aria-hidden />
            {t("auth.secure")}
          </p>
        </form>
      </div>
    </div>
  );
}
