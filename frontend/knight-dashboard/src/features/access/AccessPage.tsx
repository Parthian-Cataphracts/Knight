import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ShieldCheck, ShieldAlert, UserPlus } from "lucide-react";
import { useAction, useCollection } from "@/lib/api/hooks";
import { apiRequest } from "@/lib/api/client";
import type { AdminUser, Role } from "@/lib/api/domain";
import { PageShell, PageHeader, Toolbar, FilterTabs, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { StatusChip } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { Drawer } from "@/components/data/Drawer";
import { KeyValue } from "@/components/data/PageShell";
<<<<<<< HEAD
=======
import { EditDrawer } from "@/features/shared/EditDrawer";
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
import { useAuthStore } from "@/store/auth";
import { formatRelative } from "@/lib/utils/format";

type Tab = "users" | "roles";

export function AccessPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useState<Tab>("users");
  const users = useCollection<AdminUser>("/users");
  const roles = useCollection<Role>("/roles");
  const can = useAuthStore((state) => state.can);

  const [inviting, setInviting] = useState(false);
  const [selected, setSelected] = useState<AdminUser | null>(null);
<<<<<<< HEAD
=======
  const [renaming, setRenaming] = useState<AdminUser | null>(null);
  const [assigningRoles, setAssigningRoles] = useState<AdminUser | null>(null);
  const [creatingRole, setCreatingRole] = useState(false);
  const [editingRole, setEditingRole] = useState<Role | null>(null);
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5

  // A one-time password exists in readable form for exactly one response. It is
  // held here to be shown and never fetched again — there is no endpoint that
  // reads it back.
  const [issuedPassword, setIssuedPassword] = useState<string | null>(null);

  const accountAction = useAction<unknown, { id: string; action: string }>(
    ({ id, action }) => ({ path: `/users/${id}/${action}` }),
    ["/users"],
  );

  const resetPassword = useAction<{ temporaryPassword: string }, string>(
    (id) => ({ path: `/users/${id}/password/reset` }),
    ["/users"],
  );

  const tabs = (
    <Toolbar>
      <FilterTabs<Tab>
        value={tab}
        onChange={setTab}
        options={[
          { value: "users", label: t("access.users"), count: (users.data ?? []).length },
          { value: "roles", label: t("access.roles"), count: (roles.data ?? []).length },
        ]}
      />
    </Toolbar>
  );

  const userColumns: Column<AdminUser>[] = [
    {
      key: "user",
      header: t("access.user"),
      render: (row) => (
        <span className="flex flex-col">
          <span className="text-on-surface">{row.displayName}</span>
          <Mono>{row.email}</Mono>
        </span>
      ),
    },
    {
      key: "scope",
      header: t("access.scope"),
      render: (row) =>
        row.scope === "Platform" ? t("access.platform") : (row.customerName ?? t("access.customer")),
    },
    { key: "roles", header: t("access.rolesColumn"), render: (row) => row.roles.join("، ") },
    {
      key: "mfa",
      header: t("access.mfa"),
      render: (row) =>
        row.mfaEnabled ? (
          <span className="flex items-center gap-1.5 text-success">
            <ShieldCheck className="size-4" aria-hidden />
            {t("common.yes")}
          </span>
        ) : (
          <span className="flex items-center gap-1.5 text-warning">
            <ShieldAlert className="size-4" aria-hidden />
            {t("common.no")}
          </span>
        ),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={row.status === "Active" ? "success" : "warning"}>
          {t(`userStatus.${row.status}`)}
        </StatusChip>
      ),
    },
    {
      key: "lastSeen",
      header: t("access.lastSeen"),
      secondary: true,
      render: (row) => formatRelative(row.lastLoginAt),
    },
  ];

  const roleColumns: Column<Role>[] = [
    { key: "name", header: t("access.role"), mono: true, render: (row) => row.name },
    {
      key: "scope",
      header: t("access.scope"),
      render: (row) => (row.scope === "Platform" ? t("access.platform") : t("access.customer")),
    },
    {
      key: "system",
      header: t("access.system"),
      render: (row) => (
        <StatusChip tone={row.isSystem ? "info" : "neutral"}>
          {row.isSystem ? t("common.yes") : t("common.no")}
        </StatusChip>
      ),
    },
    {
      key: "permissions",
      header: t("access.permissions"),
      numeric: true,
      render: (row) => row.permissionCount,
    },
    { key: "users", header: t("access.userCount"), numeric: true, render: (row) => row.userCount },
  ];

  return (
    <PageShell>
      <PageHeader
        title={t("nav.access")}
        subtitle={t("access.subtitle")}
        actions={
<<<<<<< HEAD
          can("user.manage") ? (
            <Button size="sm" onClick={() => setInviting(true)}>
              <UserPlus className="size-4" aria-hidden />
              {t("access.invite")}
            </Button>
          ) : undefined
        }
      />

=======
          <>
            {tab === "users" && can("user.manage") ? (
              <Button size="sm" onClick={() => setInviting(true)}>
                <UserPlus className="size-4" aria-hidden />
                {t("access.invite")}
              </Button>
            ) : null}

            {tab === "roles" && can("role.manage") ? (
              <Button size="sm" onClick={() => setCreatingRole(true)}>
                {t("access.newRole")}
              </Button>
            ) : null}
          </>
        }
      />

      <EditDrawer
        open={renaming !== null}
        title={t("access.rename")}
        subtitle={renaming?.email}
        path={`/users/${renaming?.id ?? ""}`}
        fields={[
          {
            key: "displayName",
            label: t("access.displayName"),
            value: renaming?.displayName ?? "",
            note: t("access.renameNote"),
          },
        ]}
        onClose={() => setRenaming(null)}
        onSaved={() => {
          setRenaming(null);
          setSelected(null);
          void users.refetch();
        }}
      />

      <AssignRolesForm
        user={assigningRoles}
        roles={roles.data ?? []}
        onClose={() => setAssigningRoles(null)}
        onSaved={() => {
          setAssigningRoles(null);
          setSelected(null);
          void users.refetch();
        }}
      />

      <RoleEditor
        open={creatingRole || editingRole !== null}
        role={editingRole}
        onClose={() => {
          setCreatingRole(false);
          setEditingRole(null);
        }}
        onSaved={() => {
          setCreatingRole(false);
          setEditingRole(null);
          void roles.refetch();
        }}
      />

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
      <CreateAccountForm
        open={inviting}
        roles={roles.data ?? []}
        onClose={() => setInviting(false)}
        onCreated={(password) => {
          setInviting(false);
          setIssuedPassword(password);
          void users.refetch();
        }}
      />

      {issuedPassword ? (
        <div className="rounded-md border border-warning/40 bg-warning/10 p-4">
          <p className="text-body-sm text-on-surface">{t("access.passwordShownOnce")}</p>
          <p dir="ltr" className="mt-2 break-all font-mono text-label text-on-surface">
            {issuedPassword}
          </p>
          <Button variant="outline" size="sm" className="mt-3" onClick={() => setIssuedPassword(null)}>
            {t("common.dismiss")}
          </Button>
        </div>
      ) : null}

      {tab === "users" ? (
        <CollectionCard query={users} toolbar={tabs}>
          {(rows) => (
            <DataTable
              columns={userColumns}
              rows={rows}
              rowKey={(row) => row.id}
              onRowClick={setSelected}
              cardTitle={(row) => row.displayName}
              emptyMessage={t("common.noResults")}
            />
          )}
        </CollectionCard>
      ) : (
        <CollectionCard query={roles} toolbar={tabs}>
          {(rows) => (
            <DataTable
              columns={roleColumns}
              rows={rows}
              rowKey={(row) => row.id}
<<<<<<< HEAD
=======
              onRowClick={(row) => setEditingRole(row)}
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
              cardTitle={(row) => <Mono>{row.name}</Mono>}
              emptyMessage={t("common.noResults")}
            />
          )}
        </CollectionCard>
      )}

      <Drawer
        open={selected !== null}
        title={selected?.displayName ?? ""}
        subtitle={selected?.email}
        onClose={() => setSelected(null)}
        footer={
          can("user.manage") && selected ? (
            <>
              {selected.status === "Active" ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={accountAction.isPending}
                  onClick={() => accountAction.mutate({ id: selected.id, action: "suspend" })}
                >
                  {t("access.suspend")}
                </Button>
              ) : (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={accountAction.isPending}
                  onClick={() => accountAction.mutate({ id: selected.id, action: "activate" })}
                >
                  {t("access.activate")}
                </Button>
              )}

<<<<<<< HEAD
=======
              <Button variant="outline" size="sm" onClick={() => setRenaming(selected)}>
                {t("common.edit")}
              </Button>

              <Button variant="outline" size="sm" onClick={() => setAssigningRoles(selected)}>
                {t("access.assignRoles")}
              </Button>

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
              {selected.mfaEnabled ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={accountAction.isPending}
                  onClick={() => accountAction.mutate({ id: selected.id, action: "mfa/reset" })}
                >
                  {t("access.resetMfa")}
                </Button>
              ) : null}

              <Button
                size="sm"
                disabled={resetPassword.isPending}
                onClick={() =>
                  resetPassword.mutate(selected.id, {
                    onSuccess: (result) => {
                      setIssuedPassword(result.temporaryPassword);
                      setSelected(null);
                    },
                  })
                }
              >
                {t("access.resetPassword")}
              </Button>
            </>
          ) : undefined
        }
      >
        {selected ? (
          <div className="flex flex-col gap-4">
            {accountAction.isError || resetPassword.isError ? (
              <p
                role="alert"
                className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container"
              >
                {(accountAction.error ?? resetPassword.error)?.message}
              </p>
            ) : null}

            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("access.scope")}>
                {selected.scope === "Platform" ? t("access.platform") : (selected.customerName ?? "—")}
              </KeyValue>
              <KeyValue label={t("access.rolesColumn")}>{selected.roles.join("، ")}</KeyValue>
              <KeyValue label={t("access.mfa")}>
                {selected.mfaEnabled ? t("common.yes") : t("common.no")}
              </KeyValue>
              <KeyValue label={t("common.status")}>{t(`userStatus.${selected.status}`)}</KeyValue>
            </dl>

            {/* Said plainly: resetting is the only recovery path, and it is
                audited precisely because it is also how an account is taken
                over by somebody holding an administrator's session. */}
            <p className="text-body-sm text-on-surface-variant">{t("access.resetHint")}</p>
          </div>
        ) : null}
      </Drawer>
    </PageShell>
  );
}

/**
 * Creating an account.
 *
 * Roles are chosen here rather than afterwards: an account that exists with no
 * roles can sign in and see nothing, which reads as broken rather than
 * unfinished.
 */
function CreateAccountForm({
  open,
  roles,
  onClose,
  onCreated,
}: {
  open: boolean;
  roles: Role[];
  onClose: () => void;
  onCreated: (temporaryPassword: string) => void;
}) {
  const { t } = useTranslation();
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [roleIds, setRoleIds] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const submit = async () => {
    setSaving(true);
    setError(null);

    try {
      const created = await apiRequest<{ temporaryPassword: string }>("/users", {
        method: "POST",
        body: { email, displayName, roleIds },
      });

      setEmail("");
      setDisplayName("");
      setRoleIds([]);
      onCreated(created.temporaryPassword);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Drawer
      open={open}
      title={t("access.invite")}
      onClose={onClose}
      footer={
        <Button
          size="sm"
          disabled={saving || email.trim().length === 0 || displayName.trim().length === 0}
          onClick={() => void submit()}
        >
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
          label={t("access.email")}
          value={email}
          type="email"
          dir="ltr"
          onChange={(event) => setEmail(event.target.value)}
        />

        <TextField
          label={t("access.displayName")}
          value={displayName}
          onChange={(event) => setDisplayName(event.target.value)}
        />

        <fieldset className="flex flex-col gap-2">
          <legend className="text-body-sm font-medium text-on-surface-variant">
            {t("access.rolesColumn")}
          </legend>
          <div className="flex flex-wrap gap-2">
            {roles.map((role) => (
              <Button
                key={role.id}
                type="button"
                size="sm"
                variant={roleIds.includes(role.id) ? "primary" : "outline"}
                onClick={() =>
                  setRoleIds((current) =>
                    current.includes(role.id)
                      ? current.filter((id) => id !== role.id)
                      : [...current, role.id],
                  )
                }
              >
                {role.name}
              </Button>
            ))}
          </div>
        </fieldset>

        <p className="text-body-sm text-on-surface-variant">{t("access.inviteHint")}</p>
      </div>
    </Drawer>
  );
}
<<<<<<< HEAD
=======

/**
 * Which roles an account holds.
 *
 * The API replaces the set rather than adding to it, so this shows every role
 * with the current ones ticked - a form that only offered "add" would leave no
 * way to take one away, and taking one away is the half that matters after
 * somebody changes jobs.
 *
 * Roles are set by id, not by name. The account response carries both for that
 * reason: a client matching on the name would pick the wrong one the first time
 * a platform role and a customer role shared it.
 */
function AssignRolesForm({
  user,
  roles,
  onClose,
  onSaved,
}: {
  user: AdminUser | null;
  roles: Role[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t } = useTranslation();
  const [roleIds, setRoleIds] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  // Null until the drawer is opened on somebody, so it starts from their roles
  // rather than from the last account that was looked at.
  const chosen = roleIds ?? user?.roleIds ?? [];

  // An account belongs to a customer or to the platform, never both, and a role
  // of the other scope cannot be held.
  const offered = roles.filter((role) => role.scope === user?.scope);

  const submit = async () => {
    setSaving(true);
    setError(null);

    try {
      await apiRequest(`/users/${user?.id ?? ""}/roles`, {
        method: "PUT",
        body: { roleIds: chosen },
      });

      setRoleIds(null);
      onSaved();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Drawer
      open={user !== null}
      title={t("access.assignRoles")}
      subtitle={user?.displayName}
      onClose={() => {
        setRoleIds(null);
        onClose();
      }}
      footer={
        <Button size="sm" disabled={saving} onClick={() => void submit()}>
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

        <p className="text-body-sm text-on-surface-variant">{t("access.assignRolesNote")}</p>

        <div className="flex flex-col gap-2">
          {offered.map((role) => (
            <label key={role.id} className="flex items-start gap-2.5 text-body-sm text-on-surface">
              <input
                type="checkbox"
                className="mt-1"
                checked={chosen.includes(role.id)}
                onChange={(event) =>
                  setRoleIds(
                    event.target.checked
                      ? [...chosen, role.id]
                      : chosen.filter((id) => id !== role.id),
                  )
                }
              />
              <span className="flex flex-col">
                <Mono>{role.name}</Mono>
                {role.description ? (
                  <span className="text-on-surface-variant">{role.description}</span>
                ) : null}
              </span>
            </label>
          ))}
        </div>
      </div>
    </Drawer>
  );
}

/**
 * Creating a role, and changing what an existing one grants.
 *
 * The permission list comes from the API rather than from a constant here, for
 * the reason that endpoint exists: a role editor offering keys the server has
 * never heard of would accept a typo and grant nothing, and nobody would find
 * out until somebody could not do their job.
 *
 * A system role's permissions are fixed. It is shown, so an operator can read
 * what SuperAdmin actually grants, and it cannot be saved.
 */
function RoleEditor({
  open,
  role,
  onClose,
  onSaved,
}: {
  open: boolean;
  role: Role | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t } = useTranslation();
  const catalogue = useCollection<string>("/roles/permissions", open);

  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [scope, setScope] = useState<"Platform" | "Customer">("Platform");
  const [permissions, setPermissions] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const editing = role !== null;
  const chosen = permissions ?? role?.permissions ?? [];
  const locked = role?.isSystem === true;

  const reset = () => {
    setName("");
    setDescription("");
    setPermissions(null);
    setError(null);
  };

  const submit = async () => {
    setSaving(true);
    setError(null);

    try {
      if (editing) {
        await apiRequest(`/roles/${role.id}/permissions`, {
          method: "PUT",
          body: { permissions: chosen },
        });
      } else {
        await apiRequest("/roles", {
          method: "POST",
          body: {
            name: name.trim(),
            description: description.trim(),
            scope,
            permissions: chosen,
          },
        });
      }

      reset();
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
      title={editing ? t("access.editRole") : t("access.newRole")}
      subtitle={role?.name}
      onClose={() => {
        reset();
        onClose();
      }}
      footer={
        locked ? undefined : (
          <Button
            size="sm"
            disabled={saving || (!editing && name.trim() === "")}
            onClick={() => void submit()}
          >
            {t("common.save")}
          </Button>
        )
      }
    >
      <div className="flex flex-col gap-4">
        {error ? (
          <p role="alert" className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container">
            {error}
          </p>
        ) : null}

        {locked ? (
          <p className="rounded-md bg-surface-low px-3 py-2 text-body-sm text-on-surface-variant">
            {t("access.systemRoleNote")}
          </p>
        ) : null}

        {editing ? null : (
          <>
            <TextField
              label={t("common.name")}
              dir="ltr"
              placeholder="BillingClerk"
              value={name}
              onChange={(event) => setName(event.target.value)}
            />

            <TextField
              label={t("access.roleDescription")}
              value={description}
              onChange={(event) => setDescription(event.target.value)}
            />

            <div className="flex flex-col gap-1.5">
              <label htmlFor="role-scope" className="text-body-sm font-medium text-on-surface-variant">
                {t("access.scope")}
              </label>
              <select
                id="role-scope"
                value={scope}
                onChange={(event) => setScope(event.target.value as "Platform" | "Customer")}
                className="h-11 w-full rounded-md border border-outline-variant bg-surface-low px-3 text-body text-on-surface focus:border-primary focus:outline-none"
              >
                <option value="Platform">{t("access.platform")}</option>
                <option value="Customer">{t("access.customer")}</option>
              </select>
              <p className="text-body-sm text-on-surface-variant">{t("access.scopeNote")}</p>
            </div>
          </>
        )}

        <div className="flex flex-col gap-2">
          <p className="text-body-sm font-medium text-on-surface-variant">
            {t("access.permissions")} ({chosen.length})
          </p>

          {(catalogue.data ?? []).map((permission) => (
            <label key={permission} className="flex items-center gap-2.5 text-body-sm">
              <input
                type="checkbox"
                disabled={locked}
                checked={chosen.includes(permission)}
                onChange={(event) =>
                  setPermissions(
                    event.target.checked
                      ? [...chosen, permission]
                      : chosen.filter((key) => key !== permission),
                  )
                }
              />
              <Mono>{permission}</Mono>
            </label>
          ))}
        </div>
      </div>
    </Drawer>
  );
}
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
