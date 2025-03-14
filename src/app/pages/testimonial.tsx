import { motion } from "framer-motion";

export const Testimonials = () => {
  const testimonials = [
    { name: "John Doe", feedback: "Great service and top-notch development!" },
    { name: "Jane Smith", feedback: "They delivered exactly what we needed!" },
  ];

  return (
    <section className="w-[90%] mx-auto py-16">
      <div className="container mx-auto px-4 md:px-6 text-center">
        <h2 className="text-3xl md:text-4xl font-bold text-[#2a2a8e]">
          What Clients Say
        </h2>
        <div className="mt-8 space-y-6">
          {testimonials.map((testimonial, index) => (
            <motion.div
              key={index}
              initial={{ opacity: 0, y: 20 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.8, delay: index * 0.2 }}
              className="p-6 bg-white shadow-lg rounded-lg"
            >
              <p className="text-gray-600 italic">
                &quot;{testimonial.feedback}&quot;
              </p>
              <h4 className="mt-4 font-semibold text-[#2a2a8e]">
                {testimonial.name}
              </h4>
            </motion.div>
          ))}
        </div>
      </div>
    </section>
  );
};
