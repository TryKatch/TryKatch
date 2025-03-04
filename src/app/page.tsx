"use client";
// import HomePage from "@/app/components/homePage";
import HeroSection from "./components/heroSection";
import { SiteFooter } from "./common/footer";
import { NavBar } from "./common/navBar";
import { TeamCarousel } from "./components/teamMembers";
import ContactSection from "./components/contactForm";
import AdvantagesSection from "./components/avantage";

export default function Home() {
  return (
    <div className="flex flex-col min-h-screen">
      <NavBar />
      <main className="flex-grow min-h-[500px]">
        <HeroSection />
        <TeamCarousel />
        <div className="w-[90%] mx-auto">
          <AdvantagesSection />
        </div>
        <div id="contact">
          <ContactSection />
        </div>
      </main>
      <SiteFooter />
    </div>
  );
}
