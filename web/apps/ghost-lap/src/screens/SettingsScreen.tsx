// Settings (task 13.7): sound only. Reduced Motion and the verified controls help are later tasks; this screen says so instead of faking them.
import { BackLink, FrostPanel } from "../ui/primitives.tsx";
import { AudioControls } from "../ui/AudioControls.tsx";

export function SettingsScreen({ onBack }: { onBack: () => void }) {
  return (
    <div className="gl-settings" data-testid="settings-screen">
      <div className="gl-study-top"><BackLink onClick={onBack}>Home</BackLink></div>
      <FrostPanel className="gl-settings-panel">
        <h2>Sound</h2>
        <AudioControls />
        <p className="gl-audio-note">Reduced Motion arrives with later tasks.</p>
      </FrostPanel>
    </div>
  );
}
