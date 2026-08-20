/**
 * RFC 7807 problem document as the API actually emits it
 * (`ExceptionHandlingMiddleware`, and docs/api-contracts.md §1).
 *
 * The field names below are the ones on the wire: `errorCode`, `correlationId`
 * and `errors`. An earlier version of this file used `code`, `requestId` and
 * `validationErrors`, which the documentation had described but nothing ever
 * sent — so `validationErrors` was always undefined and every field-level
 * message the API took the trouble to return was dropped on the floor. The
 * screens showed "One or more validation errors occurred." and nothing else,
 * which is a title, not a reason.
 *
 * The older names are still read as a fallback. They cost one `??` each and
 * mean a future rename cannot silently reintroduce the same silence.
 */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;

  /** e.g. "validation_failed", "not_found", "conflict". */
  errorCode?: string;

  /** Ties the response to the server-side log line for the same request. */
  correlationId?: string;

  /** Field name -> messages. This is where the useful text lives on a 400. */
  errors?: Record<string, string[]>;

  /** Historical names, kept so a rename cannot silently break this again. */
  code?: string;
  requestId?: string;
  validationErrors?: Record<string, string[]>;
}

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly requestId: string | undefined;
  readonly validationErrors: Record<string, string[]> | undefined;

  constructor(status: number, problem: ProblemDetails) {
    super(ApiError.describe(status, problem));
    this.name = "ApiError";
    this.status = status;
    this.code = problem.errorCode ?? problem.code ?? "internal_error";
    this.requestId = problem.correlationId ?? problem.requestId;
    this.validationErrors = problem.errors ?? problem.validationErrors;
  }

  /**
   * The sentence a person reads.
   *
   * On a validation failure the title is boilerplate and the field messages are
   * the answer, so they are what gets shown. "No store has this Feature
   * installed on a different version" tells an operator what to do next;
   * "One or more validation errors occurred." does not.
   */
  private static describe(status: number, problem: ProblemDetails): string {
    const fields = problem.errors ?? problem.validationErrors;

    if (fields) {
      const messages = Object.values(fields).flat().filter(Boolean);
      if (messages.length > 0) return messages.join(" ");
    }

    return problem.detail ?? problem.title ?? `HTTP ${status}`;
  }

  get isAuthError(): boolean {
    return this.status === 401;
  }
}
