import Image from "next/image";
import { Phone } from "lucide-react";
import { Button } from "@/components/ui/button";
import Link from "next/link";

export default function HeroSection() {
  return (
    <section className="w-[90%] mx-auto bg-gray-50 py-12 md:py-24">
      <div className="container px-4 md:px-6">
        <div className="grid items-center gap-6 lg:grid-cols-2 lg:gap-12">
          <div className="flex flex-col justify-center space-y-4">
            <div className="space-y-2">
              <h1 className="text-4xl font-bold tracking-tighter text-[#2a3990] sm:text-5xl xl:text-6xl/none">
                We Code
                <br />
                We Deliver
              </h1>
            </div>
            <div className="flex items-start space-x-3 pt-4">
              <div className="rounded-full bg-[#2a3990] p-2">
                <Phone className="h-5 w-5 text-white" />
              </div>
              <div>
                <h3 className="text-xl font-semibold text-[#2a3990]">
                  Talk to us
                </h3>
                <p className="mt-2 max-w-md text-gray-600">
                  Our team of expert developers is ready to turn your ideas into
                  reality. We deliver high-quality, scalable solutions tailored
                  to your specific needs.
                </p>
                <Button
                  className="mt-4 bg-[#2a3990] hover:bg-[#1e2a6e] text-white"
                  size="lg"
                >
                  Get in touch
                </Button>
              </div>
            </div>
          </div>
          <div className="flex justify-center lg:justify-end">
            <div className="relative h-[350px] w-[350px] sm:h-[400px] sm:w-[400px] md:h-[500px] md:w-[500px]">
              <Image
                src="/hero.svg"
                alt="Developer working at desk with plants and computer"
                fill
                className="object-contain"
                priority
              />
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}
