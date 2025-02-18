import HomePage from "@/app/components/homePage";
import { SiteFooter } from "./common/footer";
import { NavBar } from "./common/navBar";
import { TeamCarousel } from "./components/teamMembers";

export default function Home() {
  return (
    <div className="flex flex-col min-h-screen">
      <NavBar />
      <main className="flex-grow min-h-[500px]">
        <HomePage />
        <TeamCarousel />
      </main>
      <SiteFooter />
    </div>
  );
}
