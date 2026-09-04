import { useMutation, useQuery } from "@tanstack/react-query";
import { apiRequest } from "@/lib/api/client";

/**
 * The customer portal's own slice of the API (docs/self-service-saas-plan.md §6).
 * Deliberately separate from the operations dashboard's `lib/api`: these are the
 * public catalogue, the self-service checkout, and the `/me` surface a merchant
 * sees about their own store — nothing about anyone else's.
 */

export interface PublicFeature {
  featureId: string;
  slug: string;
  name: string;
  description: string | null;
}

export interface PublicOptionalFeature extends PublicFeature {
  price: number | null;
  currency: string;
}

export interface PublicPlan {
  id: string;
  key: string;
  name: string;
  description: string | null;
  basePrice: number;
  currency: string;
  includedFeatures: PublicFeature[];
  optionalFeatures: PublicOptionalFeature[];
}

export interface CheckoutResponse {
  checkoutSessionId: string;
  subscriptionId: string;
  checkoutUrl: string;
  amount: number;
  currency: string;
}

export interface MeSubscription {
  id: string;
  planId: string;
  planName: string;
  status: string;
  currentPeriodEnd: string;
  cancelAtPeriodEnd: boolean;
  featureIds: string[];
}

export interface MeStore {
  id: string;
  name: string;
  slug: string;
  primaryDomain: string;
  status: string;
  integrationStatus: string;
  isReady: boolean;
}

export interface MeProvisioningStep {
  name: string;
  status: string;
}

export interface MeProvisioning {
  storeId: string;
  state: string;
  friendlyStatus: string;
  percentComplete: number;
  steps: MeProvisioningStep[];
}

export function usePublicPlans() {
  return useQuery({
    queryKey: ["portal", "catalog", "plans"],
    queryFn: () => apiRequest<PublicPlan[]>("/catalog/plans"),
  });
}

export function useMySubscription() {
  return useQuery({
    // 204 comes back as undefined — a customer who has not bought yet.
    queryKey: ["portal", "me", "subscription"],
    queryFn: () => apiRequest<MeSubscription | undefined>("/me/subscription"),
  });
}

export function useMyStores() {
  return useQuery({
    queryKey: ["portal", "me", "stores"],
    queryFn: () => apiRequest<MeStore[]>("/me/stores"),
  });
}

/**
 * Polls a store's provisioning while it is coming up, and stops once it is ready
 * or has failed — there is nothing more to watch after a terminal state.
 */
export function useProvisioning(storeId: string | undefined) {
  return useQuery({
    queryKey: ["portal", "me", "provisioning", storeId],
    queryFn: () => apiRequest<MeProvisioning>(`/me/stores/${storeId}/provisioning`),
    enabled: Boolean(storeId),
    refetchInterval: (query) => {
      const state = query.state.data?.state;
      return state === "ready" || state === "failed" ? false : 2500;
    },
  });
}

export function useRegister() {
  return useMutation({
    mutationFn: (body: { email: string; password: string; name: string; companyName?: string }) =>
      apiRequest<{ status: string }>("/auth/register", { method: "POST", body }),
  });
}

export function useVerifyEmail() {
  return useMutation({
    mutationFn: (token: string) =>
      apiRequest<{ status: string }>("/auth/verify-email", { method: "POST", body: { token } }),
  });
}

export function useCheckout() {
  return useMutation({
    mutationFn: (body: { planId: string; billingInterval: string; selectedFeatureIds: string[] }) =>
      apiRequest<CheckoutResponse>("/billing/checkout", { method: "POST", body }),
  });
}

export function useCancelSubscription() {
  return useMutation({
    mutationFn: () => apiRequest<void>("/me/subscription/cancel", { method: "POST" }),
  });
}
