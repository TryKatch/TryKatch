"use client";

import * as React from "react";
import { motion } from "framer-motion";
import { ChevronLeft, ChevronRight, User } from "lucide-react";
import { Button } from "@/components/ui/button";

interface TeamMember {
  id: number;
  name: string;
  role: string;
  image?: string;
}

const teamMembers: TeamMember[] = [
  { id: 1, name: "Jane Doe", role: "COO" },
  { id: 2, name: "Sudi David M.", role: "CEO/CTO" },
  { id: 3, name: "John Doe", role: "CFO" },
];

export function TeamCarousel() {
  const [currentIndex, setCurrentIndex] = React.useState(1); // Start with middle member

  const navigate = (direction: number) => {
    const newIndex = currentIndex + direction;
    if (newIndex >= 0 && newIndex < teamMembers.length) {
      setCurrentIndex(newIndex);
    }
  };

  return (
    <div className="relative mx-auto max-w-6xl px-4 py-16">
      <h2 className="mb-16 text-center text-3xl font-bold text-blue-600">
        Our Board Team
      </h2>

      <div className="relative h-[300px] w-[70%] m-auto">
        <div className="absolute left-0 right-0 flex items-center justify-center">
          <div className="relative h-[300px] w-full max-w-4xl">
            <div className="flex items-center justify-center gap-8">
              {teamMembers.map((member, index) => (
                <motion.div
                  key={member.id}
                  initial={false}
                  animate={{
                    scale: currentIndex === index ? 1 : 0.7,
                    opacity: currentIndex === index ? 1 : 0.5,
                  }}
                  transition={{
                    type: "spring",
                    stiffness: 300,
                    damping: 20,
                  }}
                  className="flex flex-col items-center"
                >
                  <div
                    className={`relative overflow-hidden rounded-lg bg-gray-200 transition-all duration-300 ${
                      currentIndex === index ? "h-48 w-48 p-8" : "h-32 w-32 p-6"
                    }`}
                  >
                    <User className="h-full w-full text-gray-400" />
                  </div>
                  <motion.div
                    initial={false}
                    animate={{
                      scale: currentIndex === index ? 1 : 0.9,
                    }}
                    className="mt-4 text-center"
                  >
                    <p
                      className={`font-medium transition-all ${
                        currentIndex === index ? "text-lg" : "text-base"
                      }`}
                    >
                      {member.name}
                    </p>
                    <p
                      className={`text-gray-500 transition-all ${
                        currentIndex === index ? "text-base" : "text-sm"
                      }`}
                    >
                      {member.role}
                    </p>
                  </motion.div>
                </motion.div>
              ))}
            </div>
          </div>
        </div>

        {/* Navigation Buttons */}
        {currentIndex > 0 && (
          <Button
            variant="ghost"
            size="icon"
            className="absolute left-4 top-1/2 -translate-y-1/2 transform"
            onClick={() => navigate(-1)}
          >
            <ChevronLeft className="h-6 w-6" />
          </Button>
        )}
        {currentIndex < teamMembers.length - 1 && (
          <Button
            variant="ghost"
            size="icon"
            className="absolute right-4 top-1/2 -translate-y-1/2 transform"
            onClick={() => navigate(1)}
          >
            <ChevronRight className="h-6 w-6" />
          </Button>
        )}
      </div>

      {/* Navigation Dots */}
      <div className="mt-8 flex justify-center space-x-2">
        {teamMembers.map((_, index) => (
          <button
            key={index}
            onClick={() => setCurrentIndex(index)}
            className={`h-2 rounded-full transition-all ${
              currentIndex === index ? "w-4 bg-blue-600" : "w-2 bg-gray-300"
            }`}
          >
            <span className="sr-only">Go to slide {index + 1}</span>
          </button>
        ))}
      </div>
    </div>
  );
}
