import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { apiRequest, type RequestOptions } from "@/lib/api/client";
import { Drawer } from "@/components/data/Drawer";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";

export interface EditField {
  key: string;
  label: string;
  value: string;
  /** Rendered left-to-right regardless of locale — domains, emails, identifiers. */
  ltr?: boolean;
  required?: boolean;
  placeholder?: string;
  /** Renders a select instead of a text box. For fields the API only accepts a fixed set of values for. */
  choices?: { value: string; label: string }[];
  /** Shown under the field. Use it where saving a value has a consequence worth stating before the operator saves. */
  note?: string;
<<<<<<< HEAD
=======

  /**
   * Send null rather than an empty string when the field is left blank.
   *
   * Needed for anything the API types as a nullable id: an empty string is not a
   * guid, and the request is refused before it reaches a handler. Text fields
   * mostly do not want this - the aggregates already fold blank to null - so it
   * is opt-in rather than the default.
   */
  nullWhenEmpty?: boolean;
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
}

/**
 * A small form for editing the handful of fields an aggregate lets you change.
 *
 * Shared rather than written per screen because every one of these forms is the
 * same shape — load the current values, edit a few, PATCH them back — and the
 * part worth getting right is the part that is easy to skip: showing the
 * server's refusal. A domain that is already taken, a name that is too long, a
 * store that cannot be renamed while a job is running are all decisions the
 * server makes, and a form that swallowed them would leave an operator pressing
 * save and watching nothing happen.
 *
 * Deliberately not a generic form builder. It takes flat fields — text, or a
 * fixed set of choices — because that is what these aggregates expose; anything
 * richer belongs on its own screen where it can be designed properly.
 */
export function EditDrawer({
  open,
  title,
  subtitle,
  path,
  method = "PATCH",
  fields,
  onClose,
  onSaved,
}: {
  open: boolean;
  title: string;
  subtitle?: string | undefined;
  path: string;
  method?: RequestOptions["method"];
  fields: EditField[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t } = useTranslation();
  const [values, setValues] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  // Reset whenever the drawer opens on a different subject, so the form never
  // shows the previous record's values for a moment.
  useEffect(() => {
    if (!open) return;

    setValues(Object.fromEntries(fields.map((field) => [field.key, field.value])));
    setError(null);
    // The field list is rebuilt on each render by its caller; keying the reset
    // on the path is what makes "a different subject" the trigger.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, path]);

  const missing = fields.some(
    (field) => field.required !== false && (values[field.key] ?? "").trim().length === 0,
  );

  const submit = async () => {
    setSaving(true);
    setError(null);

<<<<<<< HEAD
    try {
      await apiRequest(path, { method, body: values });
=======
    // A field flagged nullWhenEmpty sends null, not "". See EditField.
    const body: Record<string, string | null> = { ...values };
    for (const field of fields) {
      if (field.nullWhenEmpty && (body[field.key] ?? "") === "") {
        body[field.key] = null;
      }
    }

    try {
      await apiRequest(path, { method, body });
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5

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
      title={title}
      subtitle={subtitle}
      onClose={onClose}
      footer={
        <Button size="sm" disabled={saving || missing} onClick={() => void submit()}>
          {t("common.save")}
        </Button>
      }
    >
      <div className="flex flex-col gap-4">
        {error ? (
          <p
            role="alert"
            className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container"
          >
            {error}
          </p>
        ) : null}

        {fields.map((field) => (
          <div key={field.key} className="flex flex-col gap-1.5">
            {field.choices ? (
              <>
                <label
                  htmlFor={`edit-${field.key}`}
                  className="text-body-sm font-medium text-on-surface-variant"
                >
                  {field.label}
                </label>
                <select
                  id={`edit-${field.key}`}
                  value={values[field.key] ?? ""}
                  onChange={(event) =>
                    setValues((current) => ({ ...current, [field.key]: event.target.value }))
                  }
                  className="h-11 w-full rounded-md border border-outline-variant bg-surface-low px-3 text-body text-on-surface focus:border-primary focus:outline-none"
                >
                  {field.choices.map((choice) => (
                    <option key={choice.value} value={choice.value}>
                      {choice.label}
                    </option>
                  ))}
                </select>
              </>
            ) : (
              <TextField
                label={field.label}
                value={values[field.key] ?? ""}
                dir={field.ltr ? "ltr" : undefined}
                placeholder={field.placeholder}
                onChange={(event) =>
                  setValues((current) => ({ ...current, [field.key]: event.target.value }))
                }
              />
            )}

            {field.note ? (
              <p className="text-body-sm text-on-surface-variant">{field.note}</p>
            ) : null}
          </div>
        ))}
      </div>
    </Drawer>
  );
}
