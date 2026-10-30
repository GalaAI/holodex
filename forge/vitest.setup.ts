import { configure } from "@testing-library/dom";

/**
 * Studio routes are lazy-loaded. In a full parallel `pnpm test` run the first
 * import of a feature folder can take longer than Testing Library's default
 * 1 s wait, so findBy* queries get 5 s. Node-only tests are unaffected.
 */
configure({ asyncUtilTimeout: 5_000 });
