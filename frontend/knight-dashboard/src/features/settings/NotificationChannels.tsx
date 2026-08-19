import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Plus, Send } from "lucide-react";
import { useAction, useCollection } from "@/lib/api/hooks";
import { apiRequest } from "@/lib/api/client";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Drawer } from "@/components/data/Drawer";
import { KeyValue, Mono } from "@/components/data/PageShell";
import { useAuthStore } from "@/store/auth";
import { formatRelative } from "@/lib/utils/format";

interface NotificationChannel {
  id: string;
  customerId: string | null;
  name: string;
  kind: "InApp" | "Email" | "Webhook";
  endpoint: string | null;
  minimumSeverity: "Info" | "Warning" | "Critical";
  ruleFilter: string[];
  isEnabled: boolean;
  disabledReason: string | null;
  lastDeliveredAt: string | null;
  consecutiveFailures: number;
  hasSecret: boolean;
}

const severities = ["Info", "Warning", "Critical"] as const;
const kinds = ["InApp", "Email", "Webhook"] as const;

const severityTone: Record<NotificationChannel["minimumSeverity"], Tone> = {
  Critical: "danger",
  Warning: "warning",
  Info: "info",
};

/**
 * Where alerts go, and who gets told.
 *
 * The screen exists because the routing rules are the part of alerting people
 * actually tune: a severity floor and a rule filter are what stand between a
 * channel somebody reads and a channel somebody muted. Both are editable here,
 * and the rule list is fetched from the server rather than typed, because a
 * filter naming a rule that does not exist silently matches nothing and looks
 * exactly like a channel that works.
 */
export function NotificationChannels() {
  const { t } = useTranslation();
  const can = useAuthStore((state) => state.can);
  const channels = useCollection<NotificationChannel>("/notifications/channels?includeDisabled=true");
  const rules = useCollection<string>("/notifications/rules");

  const [creating, setCreating] = useState(false);
  const [selected, setSelected] = useState<NotificationChannel | null>(null);
  const [tested, setTested] = useState<{ id: string; ok: boolean; error: string | null } | null>(null);

  const toggle = useAction<unknown, { id: string; enabled: boolean }>(
    ({ id, enabled }) => ({ path: `/notifications/channels/${id}/${enabled ? "enable" : "disable"}` }),
    ["/notifications/channels"],
  );

  const test = useAction<{ succeeded: boolean; error: string | null }, string>(
    (id) => ({ path: `/notifications/channels/${id}/test` }),
    ["/notifications"],
  );

  if (!can("notification.manage")) {
    return null;
  }

  const items = channels.data ?? [];

  return (
    <Card>
      <CardHeader
        title={t("notifications.channels")}
        action={
          <Button size="sm" variant="outline" onClick={() => setCreating(true)}>
            <Plus className="size-4" />
            {t("notifications.addChannel")}
          </Button>
        }
      />
      <CardBody className="flex flex-col gap-3">
        {items.length === 0 ? (
          <p className="text-body-sm text-on-surface-variant">{t("notifications.noChannels")}</p>
        ) : (
          items.map((channel) => (
            <div
              key={channel.id}
              className="flex flex-wrap items-center gap-3 rounded-md bg-surface-low p-3"
            >
              <button
                type="button"
                className="flex min-w-0 flex-1 flex-col items-start text-start"
                onClick={() => setSelected(channel)}
              >
                <span className="text-body-sm text-on-surface">{channel.name}</span>
                <span dir="ltr" className="truncate font-mono text-label text-on-surface-variant">
                  {channel.endpoint ?? t("notifications.inAppDestination")}
                </span>
              </button>

              <StatusChip tone={severityTone[channel.minimumSeverity]}>
                {t(`severity.${channel.minimumSeverity.toLowerCase()}`)}
              </StatusChip>

              <StatusChip tone={channel.isEnabled ? "success" : "neutral"}>
                {channel.isEnabled ? t("common.enabled") : t("common.disabled")}
              </StatusChip>

              <Button
                size="sm"
                variant="outline"
                disabled={test.isPending}
                onClick={() =>
                  test.mutate(channel.id, {
                    onSuccess: (result) =>
                      setTested({ id: channel.id, ok: result.succeeded, error: result.error }),
                  })
                }
              >
                <Send className="size-4" />
                {t("notifications.test")}
              </Button>

              <Button
                size="sm"
                variant="outline"
                disabled={toggle.isPending}
                onClick={() => toggle.mutate({ id: channel.id, enabled: !channel.isEnabled })}
              >
                {channel.isEnabled ? t("common.disable") : t("common.enable")}
              </Button>

              {tested?.id === channel.id ? (
                <p
                  role="status"
                  className={`w-full text-body-sm ${tested.ok ? "text-success" : "text-error"}`}
                >
                  {tested.ok ? t("notifications.testSucceeded") : tested.error}
                </p>
              ) : null}
            </div>
          ))
        )}
      </CardBody>

      <ChannelForm
        open={creating}
        rules={rules.data ?? []}
        onClose={() => setCreating(false)}
        onSaved={() => {
          setCreating(false);
          void channels.refetch();
        }}
      />

      <Drawer
        open={selected !== null}
        title={selected?.name ?? ""}
        subtitle={selected?.kind}
        onClose={() => setSelected(null)}
      >
        {selected ? (
          <dl className="divide-y divide-outline-variant">
            <KeyValue label={t("notifications.destination")}>
              <Mono>{selected.endpoint ?? t("notifications.inAppDestination")}</Mono>
            </KeyValue>
            <KeyValue label={t("notifications.minimumSeverity")}>
              {t(`severity.${selected.minimumSeverity.toLowerCase()}`)}
            </KeyValue>
            <KeyValue label={t("notifications.ruleFilter")}>
              {selected.ruleFilter.length === 0 ? t("notifications.allRules") : selected.ruleFilter.join(", ")}
            </KeyValue>
            <KeyValue label={t("notifications.signed")}>
              {selected.hasSecret ? t("common.yes") : t("common.no")}
            </KeyValue>
            <KeyValue label={t("notifications.lastDelivered")}>
              {selected.lastDeliveredAt ? formatRelative(selected.lastDeliveredAt) : "—"}
            </KeyValue>
            <KeyValue label={t("notifications.consecutiveFailures")}>
              {selected.consecutiveFailures}
            </KeyValue>
            {selected.disabledReason ? (
              <KeyValue label={t("notifications.disabledReason")}>{selected.disabledReason}</KeyValue>
            ) : null}
          </dl>
        ) : null}
      </Drawer>
    </Card>
  );
}

/**
 * Creating a channel.
 *
 * The secret is write-only by design — the API never returns it — so the form
 * says so rather than pretending a blank field means "unchanged".
 */
function ChannelForm({
  open,
  rules,
  onClose,
  onSaved,
}: {
  open: boolean;
  rules: string[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [kind, setKind] = useState<(typeof kinds)[number]>("InApp");
  const [endpoint, setEndpoint] = useState("");
  const [severity, setSeverity] = useState<(typeof severities)[number]>("Warning");
  const [secret, setSecret] = useState("");
  const [selectedRules, setSelectedRules] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const submit = async () => {
    setSaving(true);
    setError(null);

    try {
      await apiRequest("/notifications/channels", {
        method: "POST",
        body: {
          name,
          kind,
          endpoint: kind === "InApp" ? undefined : endpoint,
          minimumSeverity: severity,
          ruleFilter: selectedRules,
          secret: secret.length > 0 ? secret : undefined,
        },
      });

      setName("");
      setEndpoint("");
      setSecret("");
      setSelectedRules([]);
      onSaved();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Drawer
      open={open}
      title={t("notifications.addChannel")}
      onClose={onClose}
      footer={
        <Button size="sm" disabled={saving || name.trim().length === 0} onClick={() => void submit()}>
          {t("common.save")}
        </Button>
      }
    >
      <div className="flex flex-col gap-4">
        {error ? (
          <p role="alert" className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container">
            {error}
          </p>
        ) : null}

        <TextField
          label={t("common.name")}
          value={name}
          onChange={(event) => setName(event.target.value)}
        />

        <fieldset className="flex flex-col gap-2">
          <legend className="text-body-sm font-medium text-on-surface-variant">
            {t("notifications.kind")}
          </legend>
          <div className="flex flex-wrap gap-2">
            {kinds.map((option) => (
              <Button
                key={option}
                type="button"
                size="sm"
                variant={kind === option ? "primary" : "outline"}
                onClick={() => setKind(option)}
              >
                {t(`notifications.kind_${option}`)}
              </Button>
            ))}
          </div>
        </fieldset>

        {kind === "InApp" ? (
          <p className="text-body-sm text-on-surface-variant">{t("notifications.inAppHint")}</p>
        ) : (
          <TextField
            label={t("notifications.destination")}
            value={endpoint}
            dir="ltr"
            placeholder={kind === "Email" ? "ops@example.com" : "https://hooks.example.com/knight"}
            onChange={(event) => setEndpoint(event.target.value)}
          />
        )}

        {kind === "Webhook" ? (
          <TextField
            label={t("notifications.secret")}
            value={secret}
            type="password"
            dir="ltr"
            onChange={(event) => setSecret(event.target.value)}
          />
        ) : null}

        <fieldset className="flex flex-col gap-2">
          <legend className="text-body-sm font-medium text-on-surface-variant">
            {t("notifications.minimumSeverity")}
          </legend>
          <div className="flex flex-wrap gap-2">
            {severities.map((option) => (
              <Button
                key={option}
                type="button"
                size="sm"
                variant={severity === option ? "primary" : "outline"}
                onClick={() => setSeverity(option)}
              >
                {t(`severity.${option.toLowerCase()}`)}
              </Button>
            ))}
          </div>
        </fieldset>

        <fieldset className="flex flex-col gap-2">
          <legend className="text-body-sm font-medium text-on-surface-variant">
            {t("notifications.ruleFilter")}
          </legend>
          <p className="text-body-sm text-on-surface-variant">{t("notifications.ruleFilterHint")}</p>
          <div className="flex flex-wrap gap-2">
            {rules.map((rule) => (
              <Button
                key={rule}
                type="button"
                size="sm"
                variant={selectedRules.includes(rule) ? "primary" : "outline"}
                onClick={() =>
                  setSelectedRules((current) =>
                    current.includes(rule)
                      ? current.filter((entry) => entry !== rule)
                      : [...current, rule],
                  )
                }
              >
                <span dir="ltr" className="font-mono text-label">
                  {rule}
                </span>
              </Button>
            ))}
          </div>
        </fieldset>
      </div>
    </Drawer>
  );
}
