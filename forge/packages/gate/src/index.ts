/**
 * @4ge/gate: the 4GE stage gate. A stage opens only when every earlier
 * stage has a satisfying receipt; receipts are verified against the build's
 * app folder before they count.
 */
export * from "./gate.js";
export * from "./store.js";
