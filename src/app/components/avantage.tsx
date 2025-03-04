import type React from "react";
import { motion, useInView } from "framer-motion";
import { useRef } from "react";
import { BarChart3, Puzzle, Lightbulb, Target } from "lucide-react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

interface AdvantageCardProps {
  icon: React.ReactNode;
  title: string;
  description: string;
  index: number;
}

const cardVariants = {
  hidden: { opacity: 0, y: 40 },
  visible: (index: number) => ({
    opacity: 1,
    y: 0,
    transition: {
      delay: index * 0.15,
      duration: 0.6,
      ease: "easeOut",
    },
  }),
};

const AdvantageCard = ({
  icon,
  title,
  description,
  index,
}: AdvantageCardProps) => {
  return (
    <motion.div
      variants={cardVariants}
      initial="hidden"
      whileInView="visible"
      viewport={{ once: true, amount: 0.2 }}
      custom={index}
      whileHover={{ scale: 1.05 }}
    >
      <Card className="border-none shadow-sm">
        <CardHeader className="pb-2">
          <div className="flex items-center gap-4">
            <div className="flex h-12 w-12 shrink-0 items-center justify-center rounded-full bg-purple-600">
              {icon}
            </div>
            <CardTitle className="text-xl">{title}</CardTitle>
          </div>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-muted-foreground">{description}</p>
        </CardContent>
      </Card>
    </motion.div>
  );
};

export default function AdvantagesSection() {
  const advantages: Omit<AdvantageCardProps, "index">[] = [
    {
      icon: <BarChart3 className="h-6 w-6 text-white" />,
      title: "Proven Record",
      description:
        "No two businesses are the same, and neither are our solutions. We tailor our services to meet your unique needs, ensuring that every project is a perfect fit.",
    },
    {
      icon: <Puzzle className="h-6 w-6 text-white" />,
      title: "Tailored Solutions",
      description:
        "No two businesses are the same, and neither are our solutions. We tailor our services to meet your unique needs, ensuring that every project is a perfect fit.",
    },
    {
      icon: <Lightbulb className="h-6 w-6 text-white" />,
      title: "Innovation at Its Core",
      description:
        "No two businesses are the same, and neither are our solutions. We tailor our services to meet your unique needs, ensuring that every project is a perfect fit.",
    },
    {
      icon: <Target className="h-6 w-6 text-white" />,
      title: "User-Centric Approach",
      description:
        "No two businesses are the same, and neither are our solutions. We tailor our services to meet your unique needs, ensuring that every project is a perfect fit.",
    },
  ];

  // Reference for section visibility tracking
  const ref = useRef(null);
  const isInView = useInView(ref, { once: true, amount: 0.2 });

  return (
    <section ref={ref} className="py-16 px-4 md:px-6 lg:px-8 bg-white">
      <div className="container mx-auto">
        <motion.div
          initial="hidden"
          animate={isInView ? "visible" : "hidden"}
          variants={{
            hidden: { opacity: 0, y: 40 },
            visible: { opacity: 1, y: 0, transition: { staggerChildren: 0.2 } },
          }}
          className="grid grid-cols-1 lg:grid-cols-12 gap-8 lg:gap-12 items-start"
        >
          <motion.div className="lg:col-span-4">
            <h2 className="text-3xl font-bold tracking-tight mb-4">
              Advantages
            </h2>
            <p className="text-muted-foreground">
              At Aldoric, we&apos;re not just a company, we&apos;re your
              dedicated partner in success. Our unwavering commitment sets us
              apart. Discover what makes us the right choice for your next
              project.
            </p>
          </motion.div>

          <div className="lg:col-span-8 grid grid-cols-1 md:grid-cols-2 gap-6">
            {advantages.map((advantage, index) => (
              <AdvantageCard
                key={index}
                icon={advantage.icon}
                title={advantage.title}
                description={advantage.description}
                index={index}
              />
            ))}
          </div>
        </motion.div>
      </div>
    </section>
  );
}
