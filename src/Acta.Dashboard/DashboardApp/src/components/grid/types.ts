import type { Snippet } from 'svelte';

export interface ColumnDef<T> {
  key: keyof T | string;
  header: string;
  class?: string;
  /** Right-align (numeric columns); the header follows the cells. */
  align?: 'right';
  /** Mute the cell when its raw field value equals the row above's (low-variance columns).
   *  Only meaningful when `key` is a real field on the row. */
  dimRepeats?: boolean;
}

export type CellMap<T> = Record<string, Snippet<[T]>>;
