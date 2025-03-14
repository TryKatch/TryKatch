// import { motion } from "framer-motion";

// export const Services = () => {
//   const services = [
//     {
//       title: "Web Development",
//       description: "Scalable and modern websites tailored for you.",
//     },
//     {
//       title: "Mobile Apps",
//       description: "High-performance mobile applications.",
//     },
//     {
//       title: "UI/UX Design",
//       description: "Intuitive and engaging user experiences.",
//     },
//   ];

//   return (
//     <section className="w-[90%] mx-auto py-16">
//       <div className="container mx-auto px-4 md:px-6 text-center">
//         <h2 className="text-3xl md:text-4xl font-bold text-[#2a2a8e]">
//           Our Services
//         </h2>
//         <div className="mt-8 grid grid-cols-1 md:grid-cols-3 gap-6">
//           {services.map((service, index) => (
//             <motion.div
//               key={index}
//               initial={{ opacity: 0, y: 20 }}
//               animate={{ opacity: 1, y: 0 }}
//               transition={{ duration: 0.8, delay: index * 0.2 }}
//               className="p-6 bg-white shadow-lg rounded-lg"
//             >
//               <h3 className="text-xl font-semibold text-[#2a2a8e]">
//                 {service.title}
//               </h3>
//               <p className="mt-2 text-gray-600">{service.description}</p>
//             </motion.div>
//           ))}
//         </div>
//       </div>
//     </section>
//   );
// };

import type React from "react";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardFooter,
  CardHeader,
} from "@/components/ui/card";
import { Code, Users, Palette } from "lucide-react";

import { motion } from "framer-motion";

interface ServiceItem {
  icon: React.ReactNode;
  title: string;
  description: string;
  features: string[];
  ctaText: string;
  ctaHref: string;
}

const services: ServiceItem[] = [
  {
    icon: <Palette className="h-8 w-8 text-primary" />,
    title: "UX/UI Design",
    description:
      "Crafting intuitive and visually appealing user experiences through research-driven design, wireframing, prototyping, and interactive interfaces that enhance user engagement.",
    features: [
      "User research and persona development",
      "Wireframing and prototyping",
      "Responsive and interactive UI design",
    ],
    ctaText: "Get a Design Consultation",
    ctaHref: "/design-consultation",
  },
  {
    icon: <Code className="h-8 w-8 text-primary" />,
    title: "Custom Software Development",
    description:
      "Tailored software solutions designed to meet your unique business requirements, from enterprise applications to mobile solutions, ensuring seamless integration and enhanced performance.",
    features: [
      "Tailored enterprise software solutions",
      "Mobile application development",
      "Cloud-based software services",
    ],
    ctaText: "Request a Free Consultation",
    ctaHref: "/consultation",
  },
  {
    icon: <Users className="h-8 w-8 text-primary" />,
    title: "IT Consulting",
    description:
      "Expert guidance on IT strategy, infrastructure assessment, and digital transformation, helping you leverage the right technology for optimal efficiency and growth.",
    features: [
      "Technology strategy and planning",
      "IT infrastructure assessments",
      "Digital transformation guidance",
    ],
    ctaText: "Schedule a Strategy Session",
    ctaHref: "/strategy-session",
  },
];

// "use client";

export function ServicesSection() {
  return (
    <section className="w-[97%] mx-auto py-12 md:py-16 lg:py-20">
      <div className="container px-4 md:px-6">
        {/* Heading Section */}
        <motion.div
          initial={{ opacity: 0, x: -50 }}
          animate={{ opacity: 1, x: 0 }}
          transition={{ duration: 1, ease: "easeOut" }}
          className="flex flex-col items-start gap-4"
        >
          <h2 className="text-3xl font-bold tracking-tight text-[#2a2a8e]">
            Our services & solutions
          </h2>
          <p className="text-muted-foreground max-w-[800px]">
            Enhance and secure your business with our professional services. We
            offer custom software development, expert IT consulting, and
            comprehensive cybersecurity solutions to support your success.
          </p>
        </motion.div>

        {/* Services Grid */}
        <motion.div
          initial="hidden"
          animate="visible"
          variants={{
            hidden: { opacity: 0 },
            visible: {
              opacity: 1,
              transition: { staggerChildren: 0.2 }, // Staggered entrance animation
            },
          }}
          className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6 mt-8"
        >
          {services.map((service, index) => (
            <motion.div
              key={index}
              variants={{
                hidden: { opacity: 0, scale: 0.8 },
                visible: { opacity: 1, scale: 1 },
              }}
              transition={{ duration: 0.6, ease: "easeOut" }}
            >
              <Card className="flex flex-col shadow-lg hover:shadow-xl transition-shadow">
                <CardHeader>
                  <div className="w-12 h-12 rounded-lg bg-[#2a2a8e]/10 flex items-center justify-center mb-4">
                    {service.icon}
                  </div>
                  <h3 className="text-xl font-bold text-[#2a2a8e]">
                    {service.title}
                  </h3>
                  <p className="text-sm text-muted-foreground">
                    {service.description}
                  </p>
                </CardHeader>
                <CardContent className="flex-1">
                  <ul className="space-y-2">
                    {service.features.map((feature, featureIndex) => (
                      <li
                        key={featureIndex}
                        className="flex items-center gap-2 text-sm"
                      >
                        <div className="h-1.5 w-1.5 rounded-full bg-[#2a2a8e]" />
                        {feature}
                      </li>
                    ))}
                  </ul>
                </CardContent>
                <CardFooter>
                  <Button
                    variant={index === 1 ? "default" : "outline"}
                    className="w-full"
                    asChild
                  >
                    <a href={service.ctaHref}>{service.ctaText} →</a>
                  </Button>
                </CardFooter>
              </Card>
            </motion.div>
          ))}
        </motion.div>
      </div>
    </section>
  );
}
