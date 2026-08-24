import { useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { ChevronLeft, Info, ShieldCheck, Globe, CreditCard, Package } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import { apiRequest } from "@/lib/api/client";
import { useQueryClient } from "@tanstack/react-query";
import type { Feature, Plan } from "@/lib/api/domain";
import { PageShell, PageHeader } from "@/components/data/PageShell";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { TextField } from "@/components/ui/TextField";
import { Button } from "@/components/ui/Button";
import { StatusChip } from "@/components/ui/StatusChip";

/**
 * Create a customer together with its first store, administrator and
 * subscription.
 *
 * The four writes are separate calls because they are four aggregates in four
 * modules, and nothing in this system spans a transaction across modules. That
 * makes the sequence interruptible: if the store call fails, the customer
 * already exists. Rather than pretend otherwise with a rollback that could
 * itself fail, a partial run reports which step stopped it and leaves the
 * operator on the customer that was created, where the rest can be added by
 * hand.
 *
 * Client validation is a convenience only — the API validates and is the
 * authority (docs/authorization.md).
 */
export function CreateCustomerPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const plans = useCollection<Plan>("/plans");
  const features = useCollection<Feature>("/features");

  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [contactEmail, setContactEmail] = useState("");
  const [adminName, setAdminName] = useState("");
  const [adminEmail, setAdminEmail] = useState("");
  const [domain, setDomain] = useState("");
  const [planKey, setPlanKey] = useState("basic");
  const [environment, setEnvironment] = useState("Production");
  const [hosting, setHosting] = useState("SharedManaged");
  const [optional, setOptional] = useState<string[]>([]);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [pending, setPending] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  // Shown once, after a successful run. There is no activation email yet, so the
  // password an administrator hands over is this one, and it is never readable
  // again from anywhere.
  const [temporaryPassword, setTemporaryPassword] = useState<string | null>(null);
  const queryClient = useQueryClient();

  const selectedPlan = (plans.data ?? []).find((plan) => plan.key === planKey);
  const optionalFeatures = (features.data ?? []).filter(
    (feature) => feature.isOptional && selectedPlan?.optionalFeatures.includes(feature.slug),
  );
  const includedFeatures = (features.data ?? []).filter((feature) =>
    selectedPlan?.includedFeatures.includes(feature.slug),
  );

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();

    const next: Record<string, string> = {};
    if (name.trim() === "") next["name"] = t("createCustomer.required");
    if (slug.trim() === "") next["slug"] = t("createCustomer.required");
    if (!contactEmail.includes("@")) next["contactEmail"] = t("createCustomer.invalidEmail");
    if (!adminEmail.includes("@")) next["adminEmail"] = t("createCustomer.invalidEmail");
    if (domain.trim() === "") next["domain"] = t("createCustomer.required");
    setErrors(next);

    if (Object.keys(next).length > 0) {
      return;
    }

    setPending(true);
    setFailure(null);

    // Named so a partial run can say which step stopped it, rather than leaving
    // the operator to work out how far it got.
    let step = t("createCustomer.step1");
    let customerId: string | null = null;

    try {
      const customer = await apiRequest<{ id: string }>("/customers", {
        method: "POST",
        body: { name: name.trim(), contactEmail: contactEmail.trim() },
      });

      customerId = customer.id;

      step = t("createCustomer.step2");
      await apiRequest("/stores", {
        method: "POST",
        body: {
          customerId: customer.id,
          name: name.trim(),
          slug: slug.trim(),
          primaryDomain: domain.trim(),
          environment,
          hostingModel: hosting,
        },
      });

      step = t("createCustomer.step3");
      const account = await apiRequest<{ temporaryPassword: string }>("/users", {
        method: "POST",
        body: {
          email: adminEmail.trim(),
          displayName: adminName.trim() === "" ? adminEmail.trim() : adminName.trim(),
          customerId: customer.id,
          roleIds: [],
        },
      });

      step = t("createCustomer.step4");

      if (selectedPlan) {
        await apiRequest("/subscriptions", {
          method: "POST",
          body: {
            customerId: customer.id,
            planId: selectedPlan.id,
            featureIds: (features.data ?? [])
              .filter((feature) => optional.includes(feature.slug))
              .map((feature) => feature.id),
          },
        });
      }

      // Everything the new customer touches is now stale.
      await queryClient.invalidateQueries();

      setTemporaryPassword(account.temporaryPassword);
    } catch (error) {
      setFailure(
        t("createCustomer.failedAt", {
          step,
          message: error instanceof Error ? error.message : String(error),
        }),
      );

      // The customer exists even though the run did not finish; sending the
      // operator there beats leaving them on a form that would create a second
      // one if they pressed the button again.
      if (customerId) {
        navigate(`/customers/${customerId}`);
      }
    } finally {
      setPending(false);
    }
  };

  if (temporaryPassword) {
    return (
      <PageShell>
        <PageHeader title={t("createCustomer.created")} subtitle={t("createCustomer.createdNote")} />
        <Card>
          <CardHeader title={t("createCustomer.firstAdmin")} icon={<ShieldCheck className="size-5" />} />
          <CardBody className="flex flex-col gap-3">
            <p className="text-body-sm text-on-surface-variant">{t("createCustomer.passwordNote")}</p>
            <code dir="ltr" className="rounded-md bg-surface-low p-3 font-mono text-body text-on-surface">
              {temporaryPassword}
            </code>
            <div className="flex flex-wrap gap-2">
              <Button type="button" onClick={() => navigate("/customers")}>
                {t("nav.customers")}
              </Button>
            </div>
          </CardBody>
        </Card>
      </PageShell>
    );
  }

  const selectClass =
    "h-11 w-full rounded-md border border-outline-variant bg-surface-low px-3 text-body text-on-surface focus:border-primary focus:outline-none";

  return (
    <PageShell>
      <Link
        to="/customers"
        className="flex w-fit items-center gap-1 text-body-sm text-on-surface-variant hover:text-on-surface"
      >
        <ChevronLeft className="size-4 rtl:-scale-x-100" aria-hidden />
        {t("nav.customers")}
      </Link>

      <PageHeader title={t("createCustomer.title")} subtitle={t("createCustomer.subtitle")} />

      <form onSubmit={onSubmit} className="grid grid-cols-1 gap-6 xl:grid-cols-3">
        <div className="flex flex-col gap-6 xl:col-span-2">
          <Card>
            <CardHeader title={t("createCustomer.basics")} icon={<Info className="size-5" />} />
            <CardBody className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <TextField
                label={t("createCustomer.name")}
                required
                value={name}
                onChange={(event) => setName(event.target.value)}
                error={errors["name"]}
              />
              <TextField
                label={t("createCustomer.slug")}
                required
                dir="ltr"
                placeholder="phoenix-verify"
                hint={t("createCustomer.slugHint")}
                value={slug}
                onChange={(event) => setSlug(event.target.value)}
                error={errors["slug"]}
              />
              <TextField
                label={t("createCustomer.contactEmail")}
                type="email"
                dir="ltr"
                required
                value={contactEmail}
                onChange={(event) => setContactEmail(event.target.value)}
                error={errors["contactEmail"]}
              />
              <div className="flex flex-col gap-1.5">
                <label htmlFor="env" className="text-body-sm font-medium text-on-surface-variant">
                  {t("stores.environment")}
                </label>
                <select
                  id="env"
                  className={selectClass}
                  value={environment}
                  onChange={(event) => setEnvironment(event.target.value)}
                >
                  <option value="Production">{t("environment.Production")}</option>
                  <option value="Staging">{t("environment.Staging")}</option>
                  <option value="Development">{t("environment.Development")}</option>
                </select>
              </div>
            </CardBody>
          </Card>

          <Card>
            <CardHeader title={t("createCustomer.firstAdmin")} icon={<ShieldCheck className="size-5" />} />
            <CardBody className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <TextField
                label={t("createCustomer.adminName")}
                required
                value={adminName}
                onChange={(event) => setAdminName(event.target.value)}
              />
              <TextField
                label={t("createCustomer.adminEmail")}
                type="email"
                dir="ltr"
                required
                value={adminEmail}
                onChange={(event) => setAdminEmail(event.target.value)}
                error={errors["adminEmail"]}
              />
              <p className="text-body-sm text-on-surface-variant sm:col-span-2">
                {t("createCustomer.adminNote")}
              </p>
            </CardBody>
          </Card>

          <Card>
            <CardHeader title={t("createCustomer.store")} icon={<Globe className="size-5" />} />
            <CardBody className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <TextField
                label={t("createCustomer.domain")}
                dir="ltr"
                placeholder="cafe1.ir"
                value={domain}
                onChange={(event) => setDomain(event.target.value)}
                error={errors["domain"]}
              />
              <div className="flex flex-col gap-1.5">
                <label htmlFor="hosting" className="text-body-sm font-medium text-on-surface-variant">
                  {t("stores.hosting")}
                </label>
                <select
                  id="hosting"
                  className={selectClass}
                  value={hosting}
                  onChange={(event) => setHosting(event.target.value)}
                >
                  <option value="SharedManaged">{t("hosting.SharedManaged")}</option>
                  <option value="DedicatedManaged">{t("hosting.DedicatedManaged")}</option>
                  <option value="CustomerManaged">{t("hosting.CustomerManaged")}</option>
                </select>
              </div>
              <p className="text-body-sm text-on-surface-variant sm:col-span-2">
                {t("createCustomer.domainNote")}
              </p>
            </CardBody>
          </Card>

          <Card>
            <CardHeader title={t("createCustomer.features")} icon={<Package className="size-5" />} />
            <CardBody className="flex flex-col gap-3">
              {includedFeatures.map((feature) => (
                <label
                  key={feature.id}
                  className="flex items-center justify-between gap-3 rounded-md bg-surface-low px-4 py-3 opacity-70"
                >
                  <span className="flex min-w-0 flex-col">
                    <span className="truncate text-body-sm text-on-surface">{feature.name}</span>
                    <span dir="ltr" className="font-mono text-label text-on-surface-variant">
                      {feature.slug}
                    </span>
                  </span>
                  <StatusChip tone="success">{t("createCustomer.includedInPlan")}</StatusChip>
                </label>
              ))}
              {optionalFeatures.map((feature) => (
                <label
                  key={feature.id}
                  className="flex cursor-pointer items-center justify-between gap-3 rounded-md bg-surface-low px-4 py-3"
                >
                  <span className="flex min-w-0 flex-col">
                    <span className="truncate text-body-sm text-on-surface">{feature.name}</span>
                    <span dir="ltr" className="font-mono text-label text-on-surface-variant">
                      {feature.slug}
                    </span>
                  </span>
                  <span className="flex items-center gap-3">
                    {feature.requiresDedicatedInfrastructure && hosting === "SharedManaged" ? (
                      <StatusChip tone="warning">{t("createCustomer.dedicatedOnly")}</StatusChip>
                    ) : null}
                    <input
                      type="checkbox"
                      className="size-4 rounded-sm border-outline-variant bg-surface-low accent-[var(--primary)]"
                      disabled={feature.requiresDedicatedInfrastructure && hosting === "SharedManaged"}
                      checked={optional.includes(feature.slug)}
                      onChange={(event) =>
                        setOptional((current) =>
                          event.target.checked
                            ? [...current, feature.slug]
                            : current.filter((slug) => slug !== feature.slug),
                        )
                      }
                    />
                  </span>
                </label>
              ))}
              {includedFeatures.length + optionalFeatures.length === 0 ? (
                <p className="text-body-sm text-on-surface-variant">{t("createCustomer.noFeatures")}</p>
              ) : null}
            </CardBody>
          </Card>
        </div>

        <div className="flex flex-col gap-6">
          <Card>
            <CardHeader title={t("createCustomer.plan")} icon={<CreditCard className="size-5" />} />
            <CardBody className="flex flex-col gap-3">
              {(plans.data ?? []).map((plan) => (
                <label
                  key={plan.id}
                  className={`flex cursor-pointer items-start gap-3 rounded-md border p-3.5 ${
                    planKey === plan.key ? "border-primary bg-primary/10" : "border-outline-variant"
                  }`}
                >
                  <input
                    type="radio"
                    name="plan"
                    className="mt-1 accent-[var(--primary)]"
                    checked={planKey === plan.key}
                    onChange={() => {
                      setPlanKey(plan.key);
                      setOptional([]);
                    }}
                  />
                  <span className="min-w-0">
                    <span className="block text-body-sm font-medium text-on-surface">{plan.name}</span>
                    <span className="block text-body-sm text-on-surface-variant">{plan.description}</span>
                  </span>
                </label>
              ))}
            </CardBody>
          </Card>

          <Card>
            <CardHeader title={t("createCustomer.summary")} />
            <CardBody className="flex flex-col gap-2 text-body-sm text-on-surface-variant">
              <p>{t("createCustomer.provisioningNote")}</p>
              <ol className="mt-1 flex list-inside list-decimal flex-col gap-1">
                <li>{t("createCustomer.step1")}</li>
                <li>{t("createCustomer.step2")}</li>
                <li>{t("createCustomer.step3")}</li>
                <li>{t("createCustomer.step4")}</li>
              </ol>
            </CardBody>
          </Card>

          {failure ? (
            <p role="alert" className="text-body-sm text-error">
              {failure}
            </p>
          ) : null}

          <div className="flex flex-wrap gap-2">
            <Button type="submit" loading={pending}>
              {t("createCustomer.submit")}
            </Button>
            <Button type="button" variant="outline" onClick={() => navigate("/customers")}>
              {t("createCustomer.cancel")}
            </Button>
          </div>
        </div>
      </form>
    </PageShell>
  );
}
