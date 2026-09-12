import {AbsoluteFill, CanvasImage, Easing, interpolate, staticFile, useCurrentFrame, useVideoConfig} from "remotion";

export const palette = {
  bg: "#06100c",
  panel: "#0b2117",
  green: "#5cf2a2",
  emerald: "#0f9d68",
  gold: "#e9c777",
  ivory: "#fffaf0",
  muted: "#a5b8ad",
};

export const Background: React.FC<{accent?: "green" | "gold"}> = ({accent = "green"}) => {
  const frame = useCurrentFrame();
  const glow = accent === "gold" ? "rgba(233,199,119,.16)" : "rgba(15,157,104,.22)";
  return (
    <AbsoluteFill style={{backgroundColor: palette.bg, overflow: "hidden"}}>
      <AbsoluteFill style={{backgroundImage: "linear-gradient(rgba(92,242,162,.045) 1px, transparent 1px), linear-gradient(90deg, rgba(92,242,162,.045) 1px, transparent 1px)", backgroundSize: "74px 74px", opacity: .55}} />
      <div style={{position: "absolute", width: 980, height: 980, borderRadius: "50%", background: `radial-gradient(circle, ${glow}, transparent 68%)`, left: -360, top: interpolate(frame, [0, 180], [-310, -220], {extrapolateLeft: "clamp", extrapolateRight: "clamp"})}} />
      <div style={{position: "absolute", width: 760, height: 760, borderRadius: "50%", border: "1px solid rgba(92,242,162,.12)", right: -260, bottom: -250, scale: interpolate(frame, [0, 180], [.92, 1.08], {extrapolateLeft: "clamp", extrapolateRight: "clamp"})}} />
      {Array.from({length: 11}).map((_, index) => <i key={index} style={{position: "absolute", width: index % 3 === 0 ? 8 : 4, height: index % 3 === 0 ? 8 : 4, borderRadius: "50%", background: index % 2 ? palette.green : palette.gold, boxShadow: `0 0 22px ${index % 2 ? palette.green : palette.gold}`, opacity: interpolate(frame, [index * 3, index * 3 + 18], [0, .65], {extrapolateLeft: "clamp", extrapolateRight: "clamp"}), left: `${8 + (index * 17) % 87}%`, top: `${14 + (index * 23) % 72}%`}} />)}
    </AbsoluteFill>
  );
};

export const Brand: React.FC<{size?: number; compact?: boolean}> = ({size = 160, compact = false}) => (
  <div style={{display: "flex", alignItems: "center", justifyContent: "center", gap: 20, direction: "rtl"}}>
    <CanvasImage src={staticFile("assets/ai-palm-mark.png")} style={{width: size, height: size, objectFit: "contain"}} />
    {compact ? <div style={{fontSize: 48, fontWeight: 900, color: palette.ivory}}>نخلة <span style={{color: palette.gold}}>AI</span></div> : null}
  </div>
);

export const SceneTitle: React.FC<{eyebrow: string; children: React.ReactNode; maxWidth?: number}> = ({eyebrow, children, maxWidth = 900}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  return <div style={{textAlign: "center", direction: "rtl", maxWidth, margin: "0 auto", opacity: interpolate(frame, [0, .55 * fps], [0, 1], {easing: Easing.bezier(.16,1,.3,1), extrapolateLeft: "clamp", extrapolateRight: "clamp"}), translate: `0 ${interpolate(frame, [0, .55 * fps], [45, 0], {easing: Easing.bezier(.16,1,.3,1), extrapolateLeft: "clamp", extrapolateRight: "clamp"})}px`}}><div style={{color: palette.gold, fontSize: 30, fontWeight: 800, marginBottom: 20}}>{eyebrow}</div><div style={{fontSize: 82, lineHeight: 1.25, fontWeight: 950, letterSpacing: -2, color: palette.ivory}}>{children}</div></div>;
};

export const AppFrame: React.FC<{src: string; width?: number; delay?: number; rotate?: number}> = ({src, width = 920, delay = 10, rotate = 0}) => {
  const frame = useCurrentFrame();
  return <div style={{width, overflow: "hidden", borderRadius: 24, border: "2px solid rgba(92,242,162,.42)", background: palette.panel, boxShadow: "0 40px 110px rgba(0,0,0,.56), 0 0 70px rgba(15,157,104,.2)", opacity: interpolate(frame, [delay, delay + 18], [0, 1], {extrapolateLeft: "clamp", extrapolateRight: "clamp"}), scale: interpolate(frame, [delay, delay + 28], [.9, 1], {easing: Easing.bezier(.16,1,.3,1), extrapolateLeft: "clamp", extrapolateRight: "clamp", output: "perceptual-scale"}), rotate: `${rotate}deg`}}><div style={{height: 38, display: "flex", alignItems: "center", gap: 8, padding: "0 16px", direction: "ltr", background: "#0c1b13", borderBottom: "1px solid rgba(255,255,255,.08)"}}><i style={{width: 9,height: 9,borderRadius: "50%",background: "#ff695f"}}/><i style={{width: 9,height: 9,borderRadius: "50%",background: palette.gold}}/><i style={{width: 9,height: 9,borderRadius: "50%",background: palette.green}}/><span style={{marginLeft: 10,color: palette.muted,fontSize: 18}}>AI Palm</span></div><CanvasImage src={staticFile(src)} style={{width, height: width * .625, display: "block"}} /></div>;
};

export const PlatformBadge: React.FC<{name: "Windows" | "Linux"}> = ({name}) => <div style={{minWidth: 330, height: 90, padding: "0 30px", display: "flex", alignItems: "center", justifyContent: "center", gap: 18, border: "2px solid rgba(233,199,119,.56)", borderRadius: 20, background: "rgba(9,31,21,.9)", color: palette.ivory, fontSize: 38, fontWeight: 850}}><span style={{color: palette.gold, fontSize: 40}}>{name === "Windows" ? "⊞" : "♙"}</span>{name}</div>;
