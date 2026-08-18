import type { ReactNode } from "react";
import { cn } from "@/lib/utils/cn";

export function Card({ className, children }: { className?: string; children: ReactNode }) {
  return <section className={cn("card-surface", className)}>{children}</section>;
}

export function CardHeader({
  title,
  action,
  icon,
}: {
  title: string;
  action?: ReactNode;
  icon?: ReactNode;
}) {
  return (
    <header className="flex items-center justify-between gap-3 border-b border-outline-variant px-5 py-4">
      <h2 className="flex items-center gap-2 text-title font-semibold text-on-surface">
        {icon ? <span className="text-primary">{icon}</span> : null}
        {title}
      </h2>
      {action}
    </header>
  );
}

export function CardBody({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn("p-5", className)}>{children}</div>;
}
