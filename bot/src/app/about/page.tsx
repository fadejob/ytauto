import type { Metadata } from "next";
import Landing from "@/components/legal/Landing";

export const metadata: Metadata = {
  title: "YtAuto — automated YouTube videos",
  description: "YtAuto plans, produces and publishes narrated videos to your own YouTube channel.",
};

export default function About() {
  return <Landing />;
}
