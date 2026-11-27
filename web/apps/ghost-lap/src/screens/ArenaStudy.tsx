// PREVIEW (development only): the 13.2 arena art study. Walls come from the static ArenaV1 render descriptor, not from a live game.
import { BackLink } from "../ui/primitives.tsx";
export function ArenaStudyHud({ onBack }: { onBack: () => void }) {
  return (
    <div className="gl-study-top">
      <BackLink onClick={onBack}>Home</BackLink>
      <span className="gl-fixture-tag" data-testid="fixture-tag">Art preview · static arena-v1 layout · not live geometry</span>
    </div>
  );
}
