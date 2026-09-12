import {Composition, Folder} from "remotion";
import {TransitionSeries, linearTiming} from "@remotion/transitions";
import {fade} from "@remotion/transitions/fade";
import {OpeningScene} from "./scenes/OpeningScene";
import {NetworkScene} from "./scenes/NetworkScene";
import {ExperienceScene} from "./scenes/ExperienceScene";
import {DownloadScene} from "./scenes/DownloadScene";

export const PromoVideo: React.FC = () => (
  <TransitionSeries>
    <TransitionSeries.Sequence durationInFrames={165} name="افتتاحية النخلة"><OpeningScene /></TransitionSeries.Sequence>
    <TransitionSeries.Transition presentation={fade()} timing={linearTiming({durationInFrames: 15})} />
    <TransitionSeries.Sequence durationInFrames={225} name="استكشاف الشبكة"><NetworkScene /></TransitionSeries.Sequence>
    <TransitionSeries.Transition presentation={fade()} timing={linearTiming({durationInFrames: 15})} />
    <TransitionSeries.Sequence durationInFrames={210} name="المحادثة والمشاركة"><ExperienceScene /></TransitionSeries.Sequence>
    <TransitionSeries.Transition presentation={fade()} timing={linearTiming({durationInFrames: 15})} />
    <TransitionSeries.Sequence durationInFrames={165} name="التنزيل"><DownloadScene /></TransitionSeries.Sequence>
  </TransitionSeries>
);

export const MyComposition: React.FC = () => (
  <>
    <Folder name="AI-Palm-Scenes">
      <Composition id="Opening" component={OpeningScene} durationInFrames={165} fps={30} width={1080} height={1350} />
      <Composition id="Network" component={NetworkScene} durationInFrames={225} fps={30} width={1080} height={1350} />
      <Composition id="Experience" component={ExperienceScene} durationInFrames={210} fps={30} width={1080} height={1350} />
      <Composition id="Download" component={DownloadScene} durationInFrames={165} fps={30} width={1080} height={1350} />
    </Folder>
    <Composition id="AIPalmPromoAR" component={PromoVideo} durationInFrames={720} fps={30} width={1080} height={1350} />
  </>
);
