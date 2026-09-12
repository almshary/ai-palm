import {AbsoluteFill, CanvasImage, Easing, interpolate, staticFile, useCurrentFrame, useVideoConfig} from "remotion";
import {Background, palette} from "../components";

export const OpeningScene: React.FC = () => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  return <AbsoluteFill style={{fontFamily: "Tahoma, Segoe UI, Arial", color: palette.ivory}}><Background accent="gold"/><AbsoluteFill style={{padding: "110px 80px", alignItems: "center", justifyContent: "center"}}><CanvasImage src={staticFile("assets/ai-palm-logo-ar.png")} style={{width: 430, height: 520, objectFit: "contain", opacity: interpolate(frame,[0,.7*fps],[0,1],{extrapolateLeft:"clamp",extrapolateRight:"clamp"}), scale: interpolate(frame,[0,1.1*fps],[.84,1],{easing:Easing.bezier(.16,1,.3,1),extrapolateLeft:"clamp",extrapolateRight:"clamp",output:"perceptual-scale"})}}/><div style={{marginTop: -20, maxWidth: 900, direction: "rtl", textAlign: "center", fontSize: 72, lineHeight: 1.4, fontWeight: 950, opacity: interpolate(frame,[1*fps,1.7*fps],[0,1],{extrapolateLeft:"clamp",extrapolateRight:"clamp"}), translate: `0 ${interpolate(frame,[1*fps,1.7*fps],[35,0],{extrapolateLeft:"clamp",extrapolateRight:"clamp"})}px`}}>الكرم عند العرب<br/><span style={{color:palette.gold}}>بدأ بنخلة…</span></div></AbsoluteFill></AbsoluteFill>;
};
