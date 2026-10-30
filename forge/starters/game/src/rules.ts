/**
 * Game rules, kept free of Phaser so they are easy to test and tune.
 * The scene asks these functions what happens; it only draws.
 */
export interface RunState {
  score: number;
  lives: number;
  best: number;
  over: boolean;
}

export function newRun(lives: number, best = 0): RunState {
  return { score: 0, lives, best, over: false };
}

export function caught(state: RunState): RunState {
  if (state.over) return state;
  const score = state.score + 1;
  return { ...state, score, best: Math.max(state.best, score) };
}

export function missed(state: RunState): RunState {
  if (state.over) return state;
  const lives = state.lives - 1;
  return { ...state, lives, over: lives <= 0 };
}

/** Items fall faster as the score climbs, capped so the game stays playable. */
export function fallSpeed(base: number, perPoint: number, score: number): number {
  return Math.min(base + perPoint * score, base * 3);
}
