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
import { Code, Users, Palette, ArrowRight, CheckCircle, Calculator, MessageCircle } from "lucide-react";
import { motion } from "framer-motion";

interface ServiceItem {
  icon: React.ReactNode;
  title: string;
  description: string;
  features: string[];
  ctaText: string;
  ctaHref: string;
  popular?: boolean;
}

const services: ServiceItem[] = [
  {
    icon: <Palette className="h-8 w-8 text-white" />,
    title: "UX/UI Design",
    description:
      "Crafting intuitive and visually appealing user experiences through research-driven design, wireframing, prototyping, and interactive interfaces that enhance user engagement.",
    features: [
      "User research and persona development",
      "Wireframing and prototyping",
      "Responsive and interactive UI design",
      "Design system creation",
    ],
    ctaText: "Get Design Consultation",
    ctaHref: "/design-consultation",
  },
  {
    icon: <Code className="h-8 w-8 text-white" />,
    title: "Custom Software Development",
    description:
      "Tailored software solutions designed to meet your unique business requirements, from enterprise applications to mobile solutions, ensuring seamless integration and enhanced performance.",
    features: [
      "Tailored enterprise software solutions",
      "Mobile application development",
      "Cloud-based software services",
      "API development and integration",
    ],
    ctaText: "Start Your Project",
    ctaHref: "/consultation",
    popular: true,
  },
  {
    icon: <Users className="h-8 w-8 text-white" />,
    title: "IT Consulting",
    description:
      "Expert guidance on IT strategy, infrastructure assessment, and digital transformation, helping you leverage the right technology for optimal efficiency and growth.",
    features: [
      "Technology strategy and planning",
      "IT infrastructure assessments",
      "Digital transformation guidance",
      "Security and compliance consulting",
    ],
    ctaText: "Schedule Strategy Session",
    ctaHref: "/strategy-session",
  },
];

const containerVariants = {
  hidden: { opacity: 0 },
  visible: {
    opacity: 1,
    transition: {
      staggerChildren: 0.2,
    },
  },
};

const cardVariants = {
  hidden: { 
    opacity: 0, 
    y: 50,
    scale: 0.9 
  },
  visible: { 
    opacity: 1, 
    y: 0,
    scale: 1,
    transition: {
      duration: 0.6,
      ease: "easeOut"
    }
  },
};

export function ServicesSection() {
  return (
    <section className="relative w-full py-16 md:py-20 lg:py-24 bg-gradient-to-br from-background via-accent/30 to-accent/50">
      {/* Background decoration */}
      <div className="absolute inset-0 overflow-hidden">
        <div className="absolute top-1/4 -left-40 w-80 h-80 bg-gradient-to-br from-blue-400/5 to-purple-400/5 rounded-full blur-3xl"></div>
        <div className="absolute bottom-1/4 -right-40 w-80 h-80 bg-gradient-to-bl from-indigo-400/5 to-blue-400/5 rounded-full blur-3xl"></div>
      </div>

      <div className="relative container mx-auto px-4 md:px-6">
        {/* Header Section */}
        <motion.div
          initial={{ opacity: 0, y: 30 }}
          whileInView={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.8, ease: "easeOut" }}
          viewport={{ once: true }}
          className="text-center max-w-4xl mx-auto mb-16"
        >
          <div className="inline-flex items-center gap-2 px-4 py-2 bg-accent border border-border rounded-full text-sm font-medium text-accent-foreground mb-6">
            <CheckCircle className="w-4 h-4" />
            Our Services & Solutions
          </div>
          
          <h2 className="text-3xl md:text-4xl lg:text-5xl font-bold tracking-tight text-foreground mb-6">
            Comprehensive Digital Solutions
            <br />
            <span className="text-brand-gradient">Tailored for Your Success</span>
          </h2>
          
          <p className="text-lg md:text-xl text-muted-foreground leading-relaxed max-w-3xl mx-auto">
            Enhance and secure your business with our professional services. We offer custom software development, 
            expert IT consulting, and comprehensive design solutions to support your digital transformation journey.
          </p>
        </motion.div>

        {/* Services Grid */}
        <motion.div
          variants={containerVariants}
          initial="hidden"
          whileInView="visible"
          viewport={{ once: true, amount: 0.2 }}
          className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-8 lg:gap-10"
        >
          {services.map((service, index) => (
            <motion.div
              key={index}
              variants={cardVariants}
              whileHover={{ 
                y: -8,
                transition: { duration: 0.3, ease: "easeOut" }
              }}
              className="relative group"
            >
              {/* Popular badge */}
              {service.popular && (
                <div className="absolute -top-4 left-1/2 -translate-x-1/2 z-10">
                  <div className="bg-gradient-to-r from-blue-600 to-purple-600 text-white px-4 py-1 rounded-full text-sm font-medium shadow-lg">
                    Most Popular
                  </div>
                </div>
              )}

              <Card className={`h-full border-0 shadow-lg hover:shadow-2xl transition-all duration-300 bg-card/80 backdrop-blur-sm ${
                service.popular ? 'ring-2 ring-blue-200 dark:ring-blue-800' : ''
              }`}>
                <CardHeader className="pb-4">
                  <div className="flex items-start justify-between mb-4">
                    <div className={`w-16 h-16 rounded-2xl flex items-center justify-center shadow-lg ${
                      service.popular 
                        ? 'bg-gradient-to-br from-blue-600 to-purple-600' 
                        : 'bg-gradient-to-br from-foreground to-foreground/80'
                    }`}>
                      {service.icon}
                    </div>
                  </div>
                  
                  <h3 className="text-xl md:text-2xl font-bold text-card-foreground mb-3">
                    {service.title}
                  </h3>
                  
                  <p className="text-muted-foreground leading-relaxed">
                    {service.description}
                  </p>
                </CardHeader>

                <CardContent className="flex-1 pb-6">
                  <div className="space-y-3">
                    <h4 className="font-semibold text-card-foreground mb-3">What&apos;s included:</h4>
                    {service.features.map((feature, featureIndex) => (
                      <div
                        key={featureIndex}
                        className="flex items-start gap-3 text-sm text-muted-foreground"
                      >
                        <CheckCircle className="w-5 h-5 text-green-500 flex-shrink-0 mt-0.5" />
                        <span>{feature}</span>
                      </div>
                    ))}
                  </div>
                </CardContent>

                <CardFooter className="pt-0">
                  <Button
                    className={`w-full group/btn transition-all duration-300 ${
                      service.popular
                        ? 'brand-gradient text-white hover:opacity-90 shadow-lg hover:shadow-xl'
                        : 'border-2 border-border bg-card text-card-foreground hover:bg-accent hover:border-border'
                    }`}
                    asChild
                  >
                    <a href={service.ctaHref} className="flex items-center justify-center">
                      {service.ctaText}
                      <ArrowRight className="ml-2 h-4 w-4 transition-transform group-hover/btn:translate-x-1" />
                    </a>
                  </Button>
                </CardFooter>
              </Card>
            </motion.div>
          ))}
        </motion.div>

        {/* Enhanced Bottom CTA */}
        <motion.div
          initial={{ opacity: 0, y: 30 }}
          whileInView={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.8, delay: 0.3 }}
          viewport={{ once: true }}
          className="text-center mt-16"
        >
          <div className="bg-card/80 backdrop-blur-sm rounded-3xl p-8 md:p-12 border border-border shadow-xl">
            <h3 className="text-2xl md:text-3xl font-bold text-card-foreground mb-4">
              Ready to Transform Your Business?
            </h3>
            <p className="text-lg text-muted-foreground mb-8 max-w-2xl mx-auto">
              Let&apos;s discuss how our expertise can help you achieve your goals. 
              Get a free consultation and project estimate tailored to your needs.
            </p>
            <div className="flex flex-col sm:flex-row gap-4 justify-center">
              <Button 
                size="lg"
                className="brand-gradient text-white hover:opacity-90 shadow-lg hover:shadow-xl px-8 py-6 text-lg group"
                asChild
              >
                <a href="#contact" className="flex items-center">
                  <MessageCircle className="mr-2 h-5 w-5" />
                  Get Free Consultation
                  <ArrowRight className="ml-2 h-5 w-5 transition-transform group-hover:translate-x-1" />
                </a>
              </Button>
              <Button 
                variant="outline" 
                size="lg"
                className="border-border text-card-foreground hover:bg-accent px-8 py-6 text-lg group"
                asChild
              >
                <a href="#contact" className="flex items-center">
                  <Calculator className="mr-2 h-5 w-5" />
                  Get Project Quote
                  <ArrowRight className="ml-2 h-5 w-5 transition-transform group-hover:translate-x-1" />
                </a>
              </Button>
            </div>
            
            {/* Trust indicators */}
            <div className="mt-8 pt-8 border-t border-border">
              <div className="grid grid-cols-3 gap-8 text-center">
                <div>
                  <div className="text-2xl font-bold text-card-foreground">24h</div>
                  <div className="text-sm text-muted-foreground">Response Time</div>
                </div>
                <div>
                  <div className="text-2xl font-bold text-card-foreground">Free</div>
                  <div className="text-sm text-muted-foreground">Initial Consultation</div>
                </div>
                <div>
                  <div className="text-2xl font-bold text-card-foreground">Custom</div>
                  <div className="text-sm text-muted-foreground">Project Quotes</div>
                </div>
              </div>
            </div>
          </div>
        </motion.div>
      </div>
    </section>
  );
}
