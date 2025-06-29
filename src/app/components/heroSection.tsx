"use client";

import { motion } from "framer-motion";
import Image from "next/image";
import { Phone, ArrowRight, Star, Users, Award } from "lucide-react";
import { Button } from "@/components/ui/button";

const stats = [
  { icon: Users, value: "200+", label: "Projects Completed" },
  { icon: Award, value: "37+", label: "Awards Won" },
  { icon: Star, value: "4.9", label: "Client Rating" },
];

export default function HeroSection() {
  return (
    <div className="relative w-full bg-gradient-to-br from-background via-accent/30 to-accent/50 py-16 md:py-20 lg:py-28 overflow-hidden">
      {/* Background decorative elements */}
      <div className="absolute inset-0 overflow-hidden">
        <div className="absolute -top-40 -right-40 w-80 h-80 bg-gradient-to-br from-blue-400/10 to-purple-400/10 rounded-full blur-3xl"></div>
        <div className="absolute -bottom-40 -left-40 w-80 h-80 bg-gradient-to-tr from-indigo-400/10 to-blue-400/10 rounded-full blur-3xl"></div>
      </div>

      <div className="relative w-[90%] mx-auto">
        <div className="container mx-auto px-4 md:px-6">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-12 lg:gap-16 items-center">
            {/* Left Section (Text & Button) */}
            <motion.div
              initial={{ opacity: 0, x: -50 }}
              animate={{ opacity: 1, x: 0 }}
              transition={{ duration: 0.8, ease: "easeOut" }}
              className="space-y-8 order-2 lg:order-1"
            >
              {/* Badge */}
              <motion.div
                initial={{ opacity: 0, y: 20 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ duration: 0.6, delay: 0.2 }}
                className="inline-flex items-center gap-2 px-4 py-2 bg-accent border border-border rounded-full text-sm font-medium text-accent-foreground"
              >
                <Star className="w-4 h-4 fill-current" />
                Trusted by 200+ companies worldwide
              </motion.div>

              {/* Main heading */}
              <div className="space-y-4">
                <motion.h1
                  initial={{ opacity: 0, y: 30 }}
                  animate={{ opacity: 1, y: 0 }}
                  transition={{ duration: 0.8, delay: 0.3 }}
                  className="text-4xl md:text-5xl lg:text-6xl xl:text-7xl font-bold tracking-tight leading-tight"
                >
                  <span className="text-foreground">We</span>{" "}
                  <span className="text-brand-gradient">Code</span>
                  <br />
                  <span className="text-foreground">We</span>{" "}
                  <span className="text-brand-gradient">Deliver</span>
                </motion.h1>
                
                <motion.p
                  initial={{ opacity: 0, y: 20 }}
                  animate={{ opacity: 1, y: 0 }}
                  transition={{ duration: 0.8, delay: 0.5 }}
                  className="text-lg md:text-xl text-muted-foreground max-w-2xl leading-relaxed"
                >
                  Transform your ideas into powerful digital solutions. Our expert team delivers 
                  high-quality, scalable software that drives business growth and innovation.
                </motion.p>
              </div>

              {/* CTA Section */}
              <motion.div
                initial={{ opacity: 0, y: 20 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ duration: 0.8, delay: 0.7 }}
                className="flex flex-col sm:flex-row gap-4 items-start sm:items-center"
              >
                <Button 
                  size="lg"
                  className="brand-gradient text-white hover:opacity-90 transition-all duration-300 shadow-lg hover:shadow-xl group px-8 py-6 text-lg"
                >
                  <Phone className="mr-2 h-5 w-5" />
                  Start Your Project
                  <ArrowRight className="ml-2 h-5 w-5 transition-transform group-hover:translate-x-1" />
                </Button>
                
                <Button 
                  variant="outline" 
                  size="lg"
                  className="border-border text-foreground hover:bg-accent px-8 py-6 text-lg"
                >
                  View Our Work
                </Button>
              </motion.div>

              {/* Stats */}
              <motion.div
                initial={{ opacity: 0, y: 30 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ duration: 0.8, delay: 0.9 }}
                className="grid grid-cols-3 gap-6 pt-8 border-t border-border"
              >
                {stats.map((stat, index) => (
                  <motion.div
                    key={stat.label}
                    initial={{ opacity: 0, scale: 0.8 }}
                    animate={{ opacity: 1, scale: 1 }}
                    transition={{ duration: 0.6, delay: 1 + index * 0.1 }}
                    className="text-center"
                  >
                    <div className="flex items-center justify-center mb-2">
                      <stat.icon className="w-5 h-5 text-blue-600 mr-1" />
                      <span className="text-2xl md:text-3xl font-bold text-foreground">
                        {stat.value}
                      </span>
                    </div>
                    <p className="text-sm text-muted-foreground font-medium">{stat.label}</p>
                  </motion.div>
                ))}
              </motion.div>
            </motion.div>

            {/* Right Section (Image) */}
            <motion.div
              initial={{ opacity: 0, scale: 0.9, y: 30 }}
              animate={{ opacity: 1, scale: 1, y: 0 }}
              transition={{ duration: 1, ease: "easeOut", delay: 0.4 }}
              className="relative order-1 lg:order-2"
            >
              <div className="relative">
                {/* Decorative background */}
                <div className="absolute inset-0 bg-gradient-to-br from-accent to-accent/50 rounded-3xl rotate-3 scale-105 opacity-50"></div>
                
                {/* Main image container */}
                <div className="relative bg-card rounded-3xl shadow-2xl overflow-hidden hover-lift border border-border">
                  <div className="aspect-[4/3] relative">
                    <Image
                      src="/trykatch-hero.png"
                      alt="Professional developer working on innovative software solutions"
                      fill
                      className="object-cover"
                      priority
                      sizes="(max-width: 768px) 100vw, (max-width: 1200px) 50vw, 50vw"
                    />
                  </div>
                </div>

                {/* Floating elements */}
                <motion.div
                  animate={{ 
                    y: [0, -10, 0],
                    rotate: [0, 2, 0]
                  }}
                  transition={{ 
                    duration: 6,
                    repeat: Infinity,
                    ease: "easeInOut"
                  }}
                  className="absolute -top-6 -left-6 bg-card rounded-2xl shadow-lg p-4 border border-border"
                >
                  <div className="flex items-center gap-3">
                    <div className="w-3 h-3 bg-green-500 rounded-full animate-pulse"></div>
                    <span className="text-sm font-medium text-card-foreground">Live Project</span>
                  </div>
                </motion.div>

                <motion.div
                  animate={{ 
                    y: [0, 10, 0],
                    rotate: [0, -2, 0]
                  }}
                  transition={{ 
                    duration: 8,
                    repeat: Infinity,
                    ease: "easeInOut",
                    delay: 2
                  }}
                  className="absolute -bottom-6 -right-6 bg-card rounded-2xl shadow-lg p-4 border border-border"
                >
                  <div className="flex items-center gap-3">
                    <Award className="w-5 h-5 text-yellow-500" />
                    <span className="text-sm font-medium text-card-foreground">Award Winner</span>
                  </div>
                </motion.div>
              </div>
            </motion.div>
          </div>
        </div>
      </div>
    </div>
  );
}
