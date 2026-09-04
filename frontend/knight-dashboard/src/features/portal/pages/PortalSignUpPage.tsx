import { useState, type FormEvent, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { Store, Mail, Lock, User, Building2, ArrowRight, MailCheck } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { ApiError } from "@/lib/api/problem";
import { useRegister } from "../api";

/**
 * Public self-service sign-up (docs/self-service-saas-plan.md §6). It never says
 * whether an address is already taken — the answer is always "check your email",
 * which is exactly what the server does too.
 */
export function PortalSignUpPage() {
  const { t } = useTranslation();
  const register = useRegister();
  const [name, setName] = useState("");
  const [companyName, setCompanyName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    register.mutate(companyName ? { email, password, name, companyName } : { email, password, name });
  };

  const error = register.error instanceof ApiError ? register.error : null;
  const fieldError = (field: string) => error?.validationErrors?.[field]?.[0];

  if (register.isSuccess) {
    return (
      <Centered>
        <div className="card-surface elevated flex flex-col items-center gap-4 p-8 text-center">
          <span className="grid size-14 place-items-center rounded-lg bg-success/15 text-success">
            <MailCheck className="size-7" aria-hidden />
          </span>
          <h1 className="text-headline font-semibold text-on-surface">{t("portal.signup.checkEmailTitle")}</h1>
          <p className="text-body-sm text-on-surface-variant">{t("portal.signup.checkEmailBody", { email })}</p>
          <Link to="/verify-email" className="text-body-sm text-primary hover:underline">
            {t("portal.signup.haveToken")}
          </Link>
        </div>
      </Centered>
    );
  }

  return (
    <Centered>
      <div className="mb-8 flex flex-col items-center gap-3 text-center">
        <span className="grid size-14 place-items-center rounded-lg bg-primary/15 text-primary">
          <Store className="size-7" aria-hidden />
        </span>
        <h1 className="text-headline font-semibold text-on-surface">{t("portal.signup.title")}</h1>
        <p className="text-body-sm text-on-surface-variant">{t("portal.signup.subtitle")}</p>
      </div>

      <form onSubmit={onSubmit} className="card-surface elevated flex flex-col gap-5 p-6">
        <TextField
          label={t("portal.signup.name")}
          required
          value={name}
          onChange={(e) => setName(e.target.value)}
          icon={<User className="size-4" aria-hidden />}
          error={fieldError("name")}
        />
        <TextField
          label={t("portal.signup.company")}
          value={companyName}
          onChange={(e) => setCompanyName(e.target.value)}
          icon={<Building2 className="size-4" aria-hidden />}
        />
        <TextField
          label={t("portal.signup.email")}
          type="email"
          autoComplete="username"
          dir="ltr"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          icon={<Mail className="size-4" aria-hidden />}
          error={fieldError("email")}
        />
        <TextField
          label={t("portal.signup.password")}
          type="password"
          autoComplete="new-password"
          dir="ltr"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          icon={<Lock className="size-4" aria-hidden />}
          error={fieldError("password")}
          hint={t("portal.signup.passwordHint")}
        />

        {error && !error.validationErrors ? (
          <p role="alert" className="rounded-md bg-error/15 px-3 py-2 text-body-sm text-error">
            {error.message}
          </p>
        ) : null}

        <Button type="submit" loading={register.isPending}>
          {t("portal.signup.submit")}
          <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
        </Button>

        <p className="text-center text-body-sm text-on-surface-variant">
          {t("portal.signup.haveAccount")}{" "}
          <Link to="/" className="text-primary hover:underline">
            {t("portal.signup.signIn")}
          </Link>
        </p>
      </form>
    </Centered>
  );
}

function Centered({ children }: { children: ReactNode }) {
  return (
    <div className="grid min-h-dvh place-items-center bg-surface px-4 py-10">
      <div className="w-full max-w-md">{children}</div>
    </div>
  );
}
