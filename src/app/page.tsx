import HomePage from "@/app/components/homePage";
import { SiteFooter } from "./common/footer";

export default function Home() {
  return (
    <div className="flex flex-col min-h-screen">
      <main className="flex-grow">
        <HomePage />
      </main>
      <SiteFooter />
    </div>
  );
}
