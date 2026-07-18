import { writable } from 'svelte/store';
import { hashParams, updateHashParams } from './router';

// Cross-view namespace scope ('' = all namespaces), mirrored in the ns hash query param.
// Namespaces may become an authorization boundary, so the scope stays explicit and shareable.
export const scope = writable(hashParams().get('ns') ?? '');

addEventListener('hashchange', () => scope.set(hashParams().get('ns') ?? ''));

export function setScope(ns: string): void {
  updateHashParams({ ns: ns || null }, 'push');
  scope.set(ns);
}
