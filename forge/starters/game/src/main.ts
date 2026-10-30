import Phaser from 'phaser';
import config from './game.json';
import { caught, fallSpeed, missed, newRun, type RunState } from './rules';

/**
 * A small, complete arcade game: move the catcher with the arrow keys, A and D,
 * or the pointer; catch what falls; three misses end the run; press Space or
 * tap to play again. Shapes only, so it runs with no art; swap in sprites
 * by loading them in preload().
 */
const COLORS = { catcher: 0x79c4b7, item: 0xe8e2d0, text: '#e8e2d0', muted: '#8a877c' };
const BEST_KEY = 'best-score';

function loadBest(): number {
  try {
    return Number(localStorage.getItem(BEST_KEY) ?? 0) || 0;
  } catch {
    return 0;
  }
}

class PlayScene extends Phaser.Scene {
  private run: RunState = newRun(config.lives, loadBest());
  private catcher!: Phaser.GameObjects.Rectangle;
  private items!: Phaser.Physics.Arcade.Group;
  private hud!: Phaser.GameObjects.Text;
  private banner!: Phaser.GameObjects.Text;
  private keys!: Phaser.Types.Input.Keyboard.CursorKeys;
  private wasd!: Record<'left' | 'right', Phaser.Input.Keyboard.Key>;
  private spawner?: Phaser.Time.TimerEvent;

  constructor() {
    super('play');
  }

  create(): void {
    const { width, height } = this.scale;
    this.add.text(16, 12, `// ${config.title.toUpperCase()}`, { fontFamily: 'monospace', fontSize: '14px', color: COLORS.muted });
    this.hud = this.add.text(16, 34, '', { fontFamily: 'system-ui, sans-serif', fontSize: '20px', color: COLORS.text });
    this.banner = this.add
      .text(width / 2, height / 2, '', { fontFamily: 'system-ui, sans-serif', fontSize: '28px', color: COLORS.text, align: 'center' })
      .setOrigin(0.5);

    this.catcher = this.add.rectangle(width / 2, height - 40, 120, 18, COLORS.catcher);
    this.physics.add.existing(this.catcher);
    const body = this.catcher.body as Phaser.Physics.Arcade.Body;
    body.setImmovable(true).setAllowGravity(false).setCollideWorldBounds(true);

    this.items = this.physics.add.group();
    this.physics.add.overlap(this.catcher, this.items, (_catcher, item) => {
      (item as Phaser.GameObjects.Rectangle).destroy();
      this.run = caught(this.run);
      this.saveBest();
    });

    const keyboard = this.input.keyboard;
    if (keyboard) {
      this.keys = keyboard.createCursorKeys();
      this.wasd = {
        left: keyboard.addKey(Phaser.Input.Keyboard.KeyCodes.A),
        right: keyboard.addKey(Phaser.Input.Keyboard.KeyCodes.D),
      };
      keyboard.on('keydown-SPACE', () => this.restartIfOver());
    }
    this.input.on('pointermove', (pointer: Phaser.Input.Pointer) => {
      if (!this.run.over) this.catcher.x = Phaser.Math.Clamp(pointer.x, 60, width - 60);
    });
    this.input.on('pointerdown', () => this.restartIfOver());

    this.startRun();
  }

  private startRun(): void {
    this.run = newRun(config.lives, this.run.best);
    this.items.clear(true, true);
    this.banner.setText('');
    this.spawner?.remove();
    this.spawner = this.time.addEvent({ delay: config.spawnEveryMs, loop: true, callback: () => this.spawn() });
  }

  private spawn(): void {
    const x = Phaser.Math.Between(24, this.scale.width - 24);
    const item = this.add.rectangle(x, -12, 22, 22, COLORS.item);
    this.items.add(item);
    const body = item.body as Phaser.Physics.Arcade.Body;
    body.setAllowGravity(false).setVelocityY(fallSpeed(config.fallSpeed, config.speedUpPerPoint, this.run.score));
  }

  private restartIfOver(): void {
    if (this.run.over) this.startRun();
  }

  private saveBest(): void {
    try {
      localStorage.setItem(BEST_KEY, String(this.run.best));
    } catch {
      // Storage can be off (private windows); the best score then lasts this session only.
    }
  }

  update(): void {
    const body = this.catcher.body as Phaser.Physics.Arcade.Body;
    const left = this.keys?.left.isDown || this.wasd?.left.isDown;
    const right = this.keys?.right.isDown || this.wasd?.right.isDown;
    body.setVelocityX(this.run.over ? 0 : left ? -480 : right ? 480 : 0);

    for (const child of this.items.getChildren()) {
      const item = child as Phaser.GameObjects.Rectangle;
      if (item.y > this.scale.height + 20) {
        item.destroy();
        this.run = missed(this.run);
      }
    }

    if (this.run.over && this.spawner) {
      this.spawner.remove();
      this.spawner = undefined;
      this.items.clear(true, true);
      this.banner.setText(`Run over. You caught ${this.run.score}.\nPress Space or tap to play again.`);
    }
    this.hud.setText(`Score ${this.run.score}   Lives ${Math.max(this.run.lives, 0)}   Best ${this.run.best}`);
  }
}

new Phaser.Game({
  type: Phaser.AUTO,
  parent: 'game',
  width: config.width,
  height: config.height,
  backgroundColor: '#0b0d0e',
  physics: { default: 'arcade', arcade: { gravity: { x: 0, y: 0 } } },
  scale: { mode: Phaser.Scale.FIT, autoCenter: Phaser.Scale.CENTER_BOTH },
  scene: [PlayScene],
});
