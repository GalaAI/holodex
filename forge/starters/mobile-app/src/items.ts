/** Pure list logic, kept apart from the screen so it is easy to test and reuse. */
export interface Item {
  id: string;
  label: string;
}

let counter = 0;

export function addItem(items: readonly Item[], label: string): Item[] {
  const trimmed = label.trim();
  if (trimmed.length === 0) return [...items];
  counter += 1;
  return [{ id: `${Date.now().toString(36)}-${counter}`, label: trimmed }, ...items];
}

export function removeItem(items: readonly Item[], id: string): Item[] {
  return items.filter((item) => item.id !== id);
}
