"use client";
// import HomePage from "@/app/components/homePage";
import HeroSection from "./components/heroSection";
import { SiteFooter } from "./common/footer";
import { NavBar } from "./common/navBar";
import { TeamCarousel } from "./components/teamMembers";
import ContactSection from "./components/contactForm";
import AdvantagesSection from "./components/avantage";
// import { AboutUs } from "./pages/about";
import { AboutSection } from "./pages/about";
import { ServicesSection } from "./pages/services";
// import { Testimonials } from "./pages/testimonial";
// import { AboutSection } from "./pages/about";

export default function Home() {
  return (
    <div className="flex flex-col min-h-screen">
      <NavBar />
      <main className="flex-grow min-h-[500px]">
        <HeroSection />
        {/* <AboutUs /> */}
        <AboutSection />
        {/* <Services /> */}
        <div id="service" className="w-[90%] mx-auto">
          <ServicesSection />
        </div>
        <div className="w-[90%] mx-auto">
          <AdvantagesSection />
        </div>
        {/* <Testimonials /> */}
        <div className="hidden md:block">
          <TeamCarousel />
        </div>
        <div id="contact">
          <ContactSection />
        </div>
      </main>
      <SiteFooter />
    </div>
  );
}
