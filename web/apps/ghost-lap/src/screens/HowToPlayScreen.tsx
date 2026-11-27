// How to Play: one static instruction page over the calm menu backdrop. Player-facing copy only.
import { BackLink, FrostPanel } from "../ui/primitives.tsx";
export function HowToPlayScreen({ onBack }: { onBack: () => void }) {
  return (
    <div className="gl-howto" data-testid="howto-screen">
      <div className="gl-howto-scroll">
        <div className="gl-howto-top"><BackLink onClick={onBack}>Home</BackLink></div>
        <FrostPanel className="gl-howto-panel">
          <h2>How to Play</h2>
          <section>
            <h3>Move around</h3>
            <p>Use <strong>WASD</strong> or the <strong>arrow keys</strong> to move your character. Press two directions together to move diagonally.</p>
          </section>
          <section>
            <h3>Collect candles</h3>
            <p>Pick up the glowing orange candles to earn points and fill your <strong>Soulfire</strong>. Moving also earns points. Soulfire has four slots.</p>
          </section>
          <section>
            <h3>Use your Burst</h3>
            <p>When Soulfire reaches <strong>4/4</strong>, press <strong>Space</strong> to unleash a Burst. It can hit nearby players. If you stay fully charged too long, your Burst happens automatically.</p>
            <p>Watch out for other players' Bursts!</p>
          </section>
          <section>
            <h3>Play Multiplayer</h3>
            <p>Create a room and share its code with your friends. Once everyone is <strong>Ready</strong>, the host starts the countdown.</p>
            <p>Two to four players compete for the highest score in <strong>90 seconds</strong>. You can also join as a spectator and watch.</p>
          </section>
          <section>
            <h3>Record a Solo Run</h3>
            <p>Choose your favourite character and play a <strong>90-second</strong> run. Finish the run to save your very own <strong>Original</strong>—a recording of the choices you made.</p>
          </section>
          <section>
            <h3>Travel through time</h3>
            <p>Open <strong>My Histories</strong> and choose an Original.</p>
            <ul>
              <li><strong>Replay:</strong> Watch your recorded run. Move the hourglass timeline to visit any moment.</li>
              <li><strong>Ghost Challenge:</strong> Start again and race alongside the glowing cyan ghost of your past.</li>
              <li><strong>Rewind &amp; Fork:</strong> Choose an earlier moment, go back there and play a different future.</li>
            </ul>
            <p>Your Original is always preserved. You can compare your new future with the old one.</p>
          </section>
          <p className="gl-howto-remember"><strong>Remember:</strong> A live run cannot be paused, but you can pause and explore a saved replay.</p>
        </FrostPanel>
      </div>
    </div>
  );
}
