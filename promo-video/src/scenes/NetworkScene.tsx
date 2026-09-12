import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from "remotion";
import {AppFrame, Background, palette, SceneTitle} from "../components";

export const NetworkScene: React.FC = () => {
  const frame = useCurrentFrame(); const {fps} = useVideoConfig();
  return <AbsoluteFill style={{fontFamily:"Tahoma, Segoe UI, Arial",color:palette.ivory}}><Background/><AbsoluteFill style={{padding:"105px 70px",alignItems:"center"}}><SceneTitle eyebrow="واليوم زرعنا نخلة من نوعٍ آخر"><span>قوة أجهزتنا،</span><br/><span style={{color:palette.gold}}>متاحة للجميع.</span></SceneTitle><div style={{marginTop:65, translate:`0 ${interpolate(frame,[1*fps,6*fps],[25,-8],{extrapolateLeft:"clamp",extrapolateRight:"clamp"})}px`}}><AppFrame src="assets/ai-palm-explore.png" width={950} delay={24}/></div><div style={{position:"absolute",bottom:82,display:"flex",gap:20,direction:"rtl"}}>{["GPU وVRAM","النموذج","متاح الآن"].map((text,index)=><div key={text} style={{padding:"15px 25px",borderRadius:999,border:"1px solid rgba(92,242,162,.3)",background:"rgba(6,20,14,.92)",color:index===2?palette.green:palette.muted,fontSize:25,fontWeight:800,opacity:interpolate(frame,[2.2*fps+index*6,2.8*fps+index*6],[0,1],{extrapolateLeft:"clamp",extrapolateRight:"clamp"})}}>{text}</div>)}</div></AbsoluteFill></AbsoluteFill>;
};
