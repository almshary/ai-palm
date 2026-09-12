import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from "remotion";
import {AppFrame, Background, palette, SceneTitle} from "../components";

export const ExperienceScene: React.FC = () => {
  const frame=useCurrentFrame(); const {fps}=useVideoConfig();
  return <AbsoluteFill style={{fontFamily:"Tahoma, Segoe UI, Arial",color:palette.ivory}}><Background accent="gold"/><AbsoluteFill style={{padding:"100px 65px",alignItems:"center"}}><SceneTitle eyebrow="كل شيء داخل التطبيق"><span>استكشف. تحدث.</span><br/><span style={{color:palette.green}}>وشارك نموذجك.</span></SceneTitle><div style={{position:"relative",width:980,height:850,marginTop:55}}><div style={{position:"absolute",left:0,top:35,opacity:interpolate(frame,[18,38],[0,1],{extrapolateLeft:"clamp",extrapolateRight:"clamp"})}}><AppFrame src="assets/ai-palm-chat.png" width={820} delay={16} rotate={-2}/></div><div style={{position:"absolute",right:0,bottom:0,opacity:interpolate(frame,[2.3*fps,3*fps],[0,1],{extrapolateLeft:"clamp",extrapolateRight:"clamp"})}}><AppFrame src="assets/ai-palm-share.png" width={690} delay={65} rotate={2}/></div></div></AbsoluteFill></AbsoluteFill>;
};
