// Validates: Spec 13 task 13.3 (character expression rules: characters.md + skill refs 04/06/11). Pure logic, no Pixi, no DOM.
import assert from "node:assert/strict";
import { test } from "node:test";
import { ExpressionState, REACTION_MS, faceForCharge, gazeVector, winnerSlots } from "../src/characters/expression.ts";

test("charge 0 and 1 share one frown; 2 neutral, 3 happy, 4 grin", () => {
  assert.equal(faceForCharge(0), "frown");
  assert.equal(faceForCharge(1), "frown");
  assert.deepEqual([2, 3, 4].map((c) => faceForCharge(c as 2 | 3 | 4)), ["neutral", "happy", "grin"]);
});

test("a 2 s override returns to the CURRENT charge, not the face it interrupted", () => {
  const e = new ExpressionState();
  e.setCharge(4, 0);
  assert.equal(e.cue("hit", 1000), true);
  assert.equal(e.face(1000), "hit");
  e.setCharge(1, 1500); // the Burst hit cost charge while the X eyes were showing
  assert.equal(e.face(1000 + REACTION_MS - 1), "hit");
  assert.equal(e.face(1000 + REACTION_MS), "frown");
});

test("a repeated event key never restarts the override; a new key does", () => {
  const e = new ExpressionState();
  e.setCharge(2, 0);
  assert.equal(e.cue("fear", 0, 5000), true);
  assert.equal(e.cue("fear", 1500, 5000), false); // duplicate / resync
  assert.equal(e.face(2000), "neutral");
  assert.equal(e.cue("fear", 1500, 5001), true);
  assert.equal(e.face(2000), "fear");
  assert.equal(e.face(3500), "neutral");
});

test("priority: winner > latest hit/fear > release > charge; hit beats fear at the same instant", () => {
  const e = new ExpressionState();
  e.setCharge(4, 0);
  e.beginRelease(0);
  assert.equal(e.face(10), "release");
  e.cue("fear", 100);
  assert.equal(e.face(110), "fear");
  e.cue("hit", 200);
  assert.equal(e.face(210), "hit"); // latest real event
  assert.equal(e.face(100 + REACTION_MS), "hit"); // fear expired, hit still running
  assert.equal(e.face(200 + REACTION_MS), "release"); // overrides gone, release still open
  e.setWinner(true);
  assert.equal(e.face(300), "winner");
  const same = new ExpressionState();
  same.cue("fear", 50);
  same.cue("hit", 50);
  assert.equal(same.face(60), "hit");
});

test("release has no invented duration: it lasts until ended, until an explicit time, or until a LATER charge change", () => {
  const open = new ExpressionState();
  open.setCharge(4, 0);
  open.beginRelease(1000);
  assert.equal(open.face(1000 + 60_000), "release");
  open.setCharge(0, 1000); // same instant: part of the release itself
  assert.equal(open.face(1001), "release");
  open.setCharge(1, 1200); // authoritative state moved on afterwards
  assert.equal(open.face(1201), "frown");
  const timed = new ExpressionState();
  timed.setCharge(4, 0);
  timed.beginRelease(0, undefined, 500);
  assert.equal(timed.face(499), "release");
  assert.equal(timed.face(500), "grin");
});

test("the ghost wears one permanent evil face whatever happens", () => {
  const g = new ExpressionState(true);
  g.setCharge(4, 0);
  g.cue("hit", 0);
  g.beginRelease(0);
  g.setWinner(true);
  assert.equal(g.face(1), "evil");
  assert.equal(g.face(1e9), "evil");
});

test("reset clears overrides, winner and remembered keys but keeps the charge", () => {
  const e = new ExpressionState();
  e.setCharge(3, 0);
  e.cue("hit", 0, 1);
  e.setWinner(true);
  e.reset();
  assert.equal(e.face(1), "happy");
  assert.equal(e.cue("hit", 2, 1), true);
});

test("gaze: stationary keeps the last direction, diagonals are unit length, no direction yet is centred", () => {
  const centre = { x: 0, y: 0 };
  assert.deepEqual(gazeVector(0, 0, centre), centre);
  const right = gazeVector(1, 0, centre);
  assert.deepEqual(right, { x: 1, y: 0 });
  assert.deepEqual(gazeVector(0, 0, right), right);
  const diag = gazeVector(-1, 1, right);
  assert.ok(Math.abs(Math.hypot(diag.x, diag.y) - 1) < 1e-12 && diag.x < 0 && diag.y > 0);
});

test("winners are every rank-1 row, so ties crown all tied players", () => {
  assert.deepEqual(winnerSlots([{ slot: 2, rank: 1 }, { slot: 0, rank: 1 }, { slot: 3, rank: 3 }, { slot: 1, rank: 4 }]), [2, 0]);
  assert.deepEqual(winnerSlots([]), []);
});

test("property: after every override has expired the face is always the current charge face (200 random event sequences)", () => {
  let seed = 20260513;
  const rnd = () => ((seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0) / 2 ** 32);
  for (let run = 0; run < 200; run++) {
    const e = new ExpressionState();
    let t = 0;
    let charge = 0;
    for (let i = 0; i < 40; i++) {
      t += Math.floor(rnd() * 900);
      const r = rnd();
      if (r < 0.3) { charge = Math.floor(rnd() * 5); e.setCharge(charge as 0 | 1 | 2 | 3 | 4, t); }
      else if (r < 0.55) e.cue("fear", t, Math.floor(rnd() * 6));
      else if (r < 0.8) e.cue("hit", t, Math.floor(rnd() * 6));
      const during = e.face(t);
      assert.ok(["frown", "neutral", "happy", "grin", "fear", "hit"].includes(during), `unexpected ${during}`);
    }
    assert.equal(e.face(t + REACTION_MS + 1), faceForCharge(charge as 0 | 1 | 2 | 3 | 4));
  }
});
