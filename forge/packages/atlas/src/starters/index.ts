import type { Blueprint } from "@4ge/contracts";
import { DESKTOP_APP_BLUEPRINT } from "./desktop-app.js";
import { GAME_BLUEPRINT } from "./game.js";
import { MOBILE_APP_BLUEPRINT } from "./mobile-app.js";
import { CRM_BLUEPRINT, ERP_BLUEPRINT } from "./records.js";
import { WEB_APP_BLUEPRINT } from "./web-app.js";
import { WEBSITE_BLUEPRINT } from "./website.js";

export { DESKTOP_APP_BLUEPRINT, GAME_BLUEPRINT, MOBILE_APP_BLUEPRINT, CRM_BLUEPRINT, ERP_BLUEPRINT, WEB_APP_BLUEPRINT, WEBSITE_BLUEPRINT };
export { defineBlueprint, layer, nameParameters } from "./define.js";

/** The Blueprints every user's Atlas starts with, in the order the studio lists them. */
export const STARTER_BLUEPRINTS: readonly Blueprint[] = [
  WEBSITE_BLUEPRINT,
  WEB_APP_BLUEPRINT,
  MOBILE_APP_BLUEPRINT,
  GAME_BLUEPRINT,
  CRM_BLUEPRINT,
  ERP_BLUEPRINT,
  DESKTOP_APP_BLUEPRINT,
];
