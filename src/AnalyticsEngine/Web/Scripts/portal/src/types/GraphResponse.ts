/** Minimal shape of a Microsoft Graph collection response (`{ value: [...] }`). */
export interface GraphResponse<T> {
  value: Array<T>;
}
