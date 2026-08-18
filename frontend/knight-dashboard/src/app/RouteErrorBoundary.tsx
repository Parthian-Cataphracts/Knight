import { Component, type ErrorInfo, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Card } from "@/components/ui/Card";
import { Button } from "@/components/ui/Button";

/**
 * Keeps one screen's failure to one screen.
 *
 * Without this, a page that reads a field the API does not send takes the whole
 * application down to a blank document — which is what happened while wiring the
 * dashboard to the real API, and which tells the user nothing. A boundary per
 * route turns that into a message next to a working shell they can navigate
 * away from (docs/frontend-architecture.md).
 */
function Fallback({ error, onRetry }: { error: Error; onRetry: () => void }) {
  const { t } = useTranslation();

  return (
    <Card className="mx-auto mt-10 w-full max-w-xl p-6">
      <div className="flex flex-col items-start gap-3" role="alert">
        <p className="text-body font-medium text-error">{t("common.screenFailed")}</p>
        <p className="text-body-sm text-on-surface-variant">{error.message}</p>
        <Button variant="outline" size="sm" onClick={onRetry}>
          {t("common.retry")}
        </Button>
      </div>
    </Card>
  );
}

interface Props {
  children: ReactNode;
  /** Changing this resets the boundary, so navigating away from a broken screen recovers. */
  resetKey?: string;
}

interface State {
  error: Error | null;
}

export class RouteErrorBoundary extends Component<Props, State> {
  override state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  override componentDidUpdate(previous: Props): void {
    if (this.state.error && previous.resetKey !== this.props.resetKey) {
      this.setState({ error: null });
    }
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    // Left in the console on purpose: this is a defect to fix, not a state to
    // live with, and swallowing it silently would hide it from whoever is
    // developing the screen.
    console.error("Screen failed to render", error, info.componentStack);
  }

  override render(): ReactNode {
    if (this.state.error) {
      return <Fallback error={this.state.error} onRetry={() => this.setState({ error: null })} />;
    }

    return this.props.children;
  }
}
