import { readable, type Readable } from 'svelte/store';
import { hashParams, updateHashParams } from './router.ts';

export interface UrlFilterStore<T extends Record<string, string>> extends Readable<T> {
  patch(values: Partial<T>): void;
  clear(): void;
}

export function createUrlFilters<T extends Record<string, string>>(
  parameters: { [K in keyof T]: string },
  defaults: T
): UrlFilterStore<T> {
  const read = (): T => {
    const params = hashParams();
    const value = { ...defaults };
    for (const key of Object.keys(parameters) as (keyof T)[]) {
      value[key] = (params.get(parameters[key]) ?? defaults[key]) as T[keyof T];
    }
    return value;
  };

  const { subscribe } = readable<T>(read(), (set) => {
    const sync = () => set(read());
    addEventListener('hashchange', sync);
    return () => removeEventListener('hashchange', sync);
  });

  const write = (values: Partial<T>) => {
    const current = read();
    const next = { ...current, ...values };
    const patch: Record<string, string | null> = {};
    for (const key of Object.keys(parameters) as (keyof T)[]) {
      patch[parameters[key]] = next[key] === defaults[key] ? null : next[key];
    }
    updateHashParams(patch, 'push');
  };

  return {
    subscribe,
    patch: write,
    clear: () => write(defaults)
  };
}
