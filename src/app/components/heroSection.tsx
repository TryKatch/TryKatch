"use client";

import { motion } from "framer-motion";
import Image from "next/image";
import { Phone } from "lucide-react";
import { Button } from "@/components/ui/button";

export default function HeroSection() {
  return (
    <div className="w-[90%] mx-auto bg-white py-12 md:py-16 lg:py-20 overflow-hidden">
      <div className="container mx-auto px-4 md:px-6">
        <div className="grid grid-cols-1 md:grid-cols-2 gap-8 items-center">
          {/* Left Section (Text & Button) */}
          <motion.div
            initial={{ opacity: 0, x: -50 }}
            animate={{ opacity: 1, x: 0 }}
            transition={{ duration: 1, ease: "easeOut" }}
            className="space-y-6 order-2 md:order-1"
          >
            <h1 className="text-4xl md:text-6xl font-bold tracking-tight text-[#2a2a8e]">
              We Code
              <br />
              We Deliver
            </h1>
            <div className="flex items-start space-x-3">
              <motion.div
                initial={{ scale: 0 }}
                animate={{ scale: 1 }}
                transition={{ duration: 0.8, ease: "backOut", delay: 0.3 }}
                className="flex-shrink-0 w-10 h-10 rounded-full bg-[#2a2a8e]/10 flex items-center justify-center"
              >
                <Phone className="h-5 w-5 text-[#2a2a8e]" />
              </motion.div>
              <div>
                <h3 className="text-lg font-semibold text-[#2a2a8e]">
                  Talk to us
                </h3>
                <p className="mt-2 max-w-md text-gray-600">
                  Our team of expert developers is ready to turn your ideas into
                  reality. We deliver high-quality, scalable solutions tailored
                  to your specific needs.
                </p>
              </div>
            </div>
            <div className="pt-4">
              <motion.div
                initial={{ opacity: 0, y: 20 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ duration: 1, ease: "easeOut", delay: 0.5 }}
              >
                <Button className="bg-[#2a2a8e] hover:bg-[#1f1f6e] text-white">
                  Contact Us
                </Button>
              </motion.div>
            </div>
          </motion.div>

          {/* Right Section (Image) */}
          <motion.div
            initial={{ opacity: 0, scale: 0.8, y: 20 }}
            animate={{ opacity: 1, scale: 1, y: 0 }}
            transition={{ duration: 1, ease: "easeOut", delay: 0.2 }}
            className="relative h-[300px] md:h-[400px] lg:h-[450px] order-1 md:order-2 mb-6 md:mb-0"
          >
            <Image
              src="/hero.svg"
              alt="Developer working at desk with plants and computer"
              fill
              className="object-contain"
              priority
            />
          </motion.div>
        </div>
      </div>
    </div>
  );
}
