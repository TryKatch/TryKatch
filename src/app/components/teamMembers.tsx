"use client";

import * as React from "react";
import { motion } from "framer-motion";
import { ChevronLeft, ChevronRight, User, Linkedin, Github } from "lucide-react";
import { Button } from "@/components/ui/button";
import { TEAM_MEMBERS } from "@/constants";
import Image from "next/image";

export function TeamCarousel() {
  const [currentIndex, setCurrentIndex] = React.useState(0);

  const navigate = (direction: number) => {
    const newIndex = currentIndex + direction;
    if (newIndex >= 0 && newIndex < TEAM_MEMBERS.length) {
      setCurrentIndex(newIndex);
    }
  };

  const getIconComponent = (iconName: string) => {
    const icons: { [key: string]: React.ComponentType<any> } = {
      Linkedin,
      Github,
    };
    return icons[iconName] || Github;
  };

  return (
    <section id="team" className="relative mx-auto max-w-6xl px-4 py-16">
      <motion.div
        initial={{ opacity: 0, y: 20 }}
        whileInView={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.6 }}
        viewport={{ once: true }}
        className="text-center mb-16"
      >
        <h2 className="text-3xl font-bold text-gray-900 dark:text-white mb-4">
          Meet Our Team
        </h2>
        <p className="text-lg text-gray-600 dark:text-gray-300 max-w-2xl mx-auto">
          Our experienced team of professionals is dedicated to delivering exceptional software solutions
        </p>
      </motion.div>

      <div className="relative h-[400px] w-[80%] mx-auto">
        <div className="absolute left-0 right-0 flex items-center justify-center">
          <div className="relative h-[400px] w-full max-w-4xl">
            <div className="flex items-center justify-center gap-8">
              {TEAM_MEMBERS.map((member, index) => (
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
                    className={`relative overflow-hidden rounded-lg bg-gradient-to-br from-blue-50 to-indigo-100 dark:from-blue-900/20 dark:to-indigo-900/20 border border-gray-200 dark:border-gray-700 transition-all duration-300 ${
                      currentIndex === index ? "h-48 w-48 p-2" : "h-32 w-32 p-2"
                    }`}
                  >
                    {member.image ? (
                      <Image
                        src={member.image}
                        alt={member.name}
                        fill
                        className="object-cover rounded-md"
                        sizes="(max-width: 768px) 128px, 192px"
                      />
                    ) : (
                      <div className="h-full w-full flex items-center justify-center">
                        <User className={`text-gray-400 ${currentIndex === index ? "h-24 w-24" : "h-16 w-16"}`} />
                      </div>
                    )}
                  </div>
                  
                  <motion.div
                    initial={false}
                    animate={{
                      scale: currentIndex === index ? 1 : 0.9,
                    }}
                    className="mt-4 text-center max-w-xs"
                  >
                    <p
                      className={`font-semibold transition-all text-gray-900 dark:text-white ${
                        currentIndex === index ? "text-xl" : "text-lg"
                      }`}
                    >
                      {member.name}
                    </p>
                    <p
                      className={`text-blue-600 dark:text-blue-400 font-medium transition-all ${
                        currentIndex === index ? "text-base mb-2" : "text-sm"
                      }`}
                    >
                      {member.role}
                    </p>
                    
                    {currentIndex === index && (
                      <motion.div
                        initial={{ opacity: 0, height: 0 }}
                        animate={{ opacity: 1, height: "auto" }}
                        transition={{ delay: 0.2 }}
                      >
                        <p className="text-sm text-gray-600 dark:text-gray-300 mb-3 leading-relaxed">
                          {member.bio}
                        </p>
                        
                        {member.socialLinks && member.socialLinks.length > 0 && (
                          <div className="flex justify-center space-x-3">
                            {member.socialLinks.map((link) => {
                              const IconComponent = getIconComponent(link.icon);
                              return (
                                <a
                                  key={link.name}
                                  href={link.href}
                                  target="_blank"
                                  rel="noopener noreferrer"
                                  className="p-2 rounded-full bg-gray-100 dark:bg-gray-800 hover:bg-blue-100 dark:hover:bg-blue-900/30 transition-colors"
                                  aria-label={`${member.name} ${link.name}`}
                                >
                                  <IconComponent className="h-4 w-4 text-gray-600 dark:text-gray-300" />
                                </a>
                              );
                            })}
                          </div>
                        )}
                      </motion.div>
                    )}
                  </motion.div>
                </motion.div>
              ))}
            </div>
          </div>
        </div>

        {/* Navigation Buttons */}
        {currentIndex > 0 && (
          <Button
            variant="outline"
            size="icon"
            className="absolute left-4 top-1/2 -translate-y-1/2 transform bg-white/80 dark:bg-gray-800/80 backdrop-blur-sm hover:bg-white dark:hover:bg-gray-800"
            onClick={() => navigate(-1)}
          >
            <ChevronLeft className="h-6 w-6" />
          </Button>
        )}
        {currentIndex < TEAM_MEMBERS.length - 1 && (
          <Button
            variant="outline"
            size="icon"
            className="absolute right-4 top-1/2 -translate-y-1/2 transform bg-white/80 dark:bg-gray-800/80 backdrop-blur-sm hover:bg-white dark:hover:bg-gray-800"
            onClick={() => navigate(1)}
          >
            <ChevronRight className="h-6 w-6" />
          </Button>
        )}
      </div>

      {/* Navigation Dots */}
      <div className="mt-8 flex justify-center space-x-2">
        {TEAM_MEMBERS.map((_, index) => (
          <button
            key={index}
            onClick={() => setCurrentIndex(index)}
            className={`h-2 rounded-full transition-all ${
              currentIndex === index ? "w-4 bg-blue-600" : "w-2 bg-gray-300 dark:bg-gray-600"
            }`}
          >
            <span className="sr-only">View {TEAM_MEMBERS[index].name}</span>
          </button>
        ))}
      </div>
    </section>
  );
}
