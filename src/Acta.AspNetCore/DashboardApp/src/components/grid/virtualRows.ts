export interface IndexedVirtualRow {
  index: number;
  start: number;
  end: number;
}

export interface RenderedVirtualRow<T, TVirtualRow extends IndexedVirtualRow> {
  item: T;
  key: string | number;
  virtualRow: TVirtualRow;
}

export function renderedVirtualRows<T, TVirtualRow extends IndexedVirtualRow>(
  virtualRows: readonly TVirtualRow[],
  items: readonly T[],
  rowKey: (row: T) => string | number
): RenderedVirtualRow<T, TVirtualRow>[] {
  const rendered: RenderedVirtualRow<T, TVirtualRow>[] = [];
  for (const virtualRow of virtualRows) {
    const item = items[virtualRow.index];
    if (item === undefined) continue;
    rendered.push({ item, key: rowKey(item), virtualRow });
  }
  return rendered;
}
