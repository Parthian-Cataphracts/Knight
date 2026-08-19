import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Bell } from "lucide-react";
import { useCollection, useAction } from "@/lib/api/hooks";
import { onRealtime, type RealtimeNotification } from "@/lib/realtime/connection";
import { Drawer } from "@/components/data/Drawer";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { formatRelative } from "@/lib/utils/format";

interface NotificationDelivery {
  id: string;
  severity: "Info" | "Warning" | "Critical";
  ruleKey: string;
  title: string;
  body: string;
  status: string;
  createdAt: string;
  readAt: string | null;
}

const severityTone: Record<NotificationDelivery["severity"], Tone> = {
  Critical: "danger",
  Warning: "warning",
  Info: "info",
};

/**
 * The bell, and what is behind it.
 *
 * The list is fetched, not accumulated from the realtime channel. That ordering
 * matters: a notification that arrived while the operator had the tab closed is
 * exactly the one they need, and a client-side list would have missed it. The
 * live channel only tells the component that its data is stale, so an operator
 * watching the page sees the same thing as one who has just opened it.
 */
export function NotificationCentre() {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const query = useCollection<NotificationDelivery>("/notifications?status=Delivered&pageSize=50");

  const markRead = useAction<unknown, string>(
    (id) => ({ path: `/notifications/${id}/read` }),
    ["/notifications"],
  );

  useEffect(() => {
    // A push means "there is something new", never "here is the new thing".
    return onRealtime<RealtimeNotification>("notificationReceived", () => {
      void query.refetch();
    });
    // The refetch closure is stable enough for this: re-subscribing on every
    // render would tear the hub handler down and back up continuously.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const items = query.data ?? [];
  const unread = items.filter((item) => item.readAt === null);

  return (
    <>
      <button
        type="button"
        aria-label={t("notifications.open")}
        onClick={() => setOpen(true)}
        className="relative inline-flex size-9 items-center justify-center rounded-full text-on-surface-variant transition-colors hover:bg-surface-low hover:text-on-surface"
      >
        <Bell className="size-5" aria-hidden />
        {unread.length > 0 ? (
          <span
            aria-hidden
            className="absolute end-1.5 top-1.5 inline-flex min-w-4 items-center justify-center rounded-full bg-error px-1 text-[10px] font-medium text-on-error"
          >
            {unread.length > 9 ? "9+" : unread.length}
          </span>
        ) : null}
      </button>

      <Drawer open={open} title={t("notifications.title")} onClose={() => setOpen(false)}>
        {items.length === 0 ? (
          <p className="text-body-sm text-on-surface-variant">{t("notifications.empty")}</p>
        ) : (
          <ul className="flex flex-col gap-3">
            {items.map((item) => (
              <li
                key={item.id}
                className={`rounded-md p-3 ${item.readAt === null ? "bg-surface-low" : "bg-surface-lowest"}`}
              >
                <div className="flex flex-wrap items-center gap-2">
                  <StatusChip tone={severityTone[item.severity]}>{item.severity}</StatusChip>
                  <span dir="ltr" className="font-mono text-label text-on-surface-variant">
                    {item.ruleKey}
                  </span>
                  <span className="text-label text-on-surface-variant">
                    {formatRelative(item.createdAt)}
                  </span>
                </div>

                <p className="mt-2 text-body-sm text-on-surface">{item.title}</p>
                <p className="mt-1 text-body-sm text-on-surface-variant">{item.body}</p>

                {item.readAt === null ? (
                  <Button
                    variant="outline"
                    size="sm"
                    className="mt-2"
                    disabled={markRead.isPending}
                    onClick={() => markRead.mutate(item.id)}
                  >
                    {t("notifications.markRead")}
                  </Button>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </Drawer>
    </>
  );
}
