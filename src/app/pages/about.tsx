// export const AboutUs = () => {
//   return (
//     <section className="w-[90%] mx-auto py-16 bg-gray-50">
//       <div className="container mx-auto px-4 md:px-6 text-center">
//         <h2 className="text-3xl md:text-4xl font-bold text-[#2a2a8e]">
//           About Us
//         </h2>
//         <p className="mt-4 text-gray-600 max-w-2xl mx-auto">
//           We are a team of skilled developers committed to delivering scalable,
//           high-quality digital solutions that drive business success.
//         </p>
//       </div>
//     </section>
//   );
// };

import { Button } from "@/components/ui/button";

interface StatProps {
  number: string;
  text: string;
}

function StatBox({ number, text }: StatProps) {
  return (
    <div className="">
      <h3 className="text-4xl font-bold mb-2">{number}</h3>
      <p className="text-sm uppercase tracking-wider text-[#2a2a8e]">{text}</p>
    </div>
  );
}

export function AboutSection() {
  return (
    <section className="w-full py-20 text-gray-600">
      <div className="container px-4 md:px-6">
        <div className="text-center max-w-3xl mx-auto">
          <h2 className="text-3xl md:text-4xl font-bold mb-2">Our Passion</h2>
          <h3 className="text-3xl md:text-4xl font-bold mb-16">
            Proven <span className="text-[#2a2a8e]">Results</span>
          </h3>

          <div className="grid grid-cols-1 md:grid-cols-3 gap-6 mb-16">
            <StatBox number="200+" text="Successful Projects" />
            <StatBox number="037+" text="Awards Winning Agency" />
            <StatBox number="019+" text="Design Specialised Expert" />
          </div>

          <p className="text-lg md:text-xl leading-relaxed mb-12">
            At Our Digital Agency, We Specialize In Creating Impactful Digital
            Experiences That Drive Results. With A Talented Team Of Designers,
            Developers, And Strategists, We Offer End-To-End Solutions—From
            Branding And Web Design To Digital Marketing And AI-Driven
            Innovations.
          </p>

          <Button className="bg-gradient-to-r from-primary to-blue-800 hover:from-primary/90 hover:to-blue-500/90 text-white px-8 py-6 text-lg rounded-full">
            Book A Call
          </Button>
        </div>
      </div>
    </section>
  );
}
