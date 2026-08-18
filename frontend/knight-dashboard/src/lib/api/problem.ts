/** RFC 7807 problem document as defined in docs/api-contracts.md section 1. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  code?: string;
  detail?: string;
  requestId?: string;
  validationErrors?: Record<string, string[]>;
}

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly requestId: string | undefined;
  readonly validationErrors: Record<string, string[]> | undefined;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `HTTP ${status}`);
    this.name = "ApiError";
    this.status = status;
    this.code = problem.code ?? "internal_error";
    this.requestId = problem.requestId;
    this.validationErrors = problem.validationErrors;
  }

  get isAuthError(): boolean {
    return this.status === 401;
  }
}
