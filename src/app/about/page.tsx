"use client";

import Image from "next/image";
import Link from "next/link";
import { motion } from "framer-motion";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { NavBar } from "../common/navBar";
import { SiteFooter } from "../common/footer";

export default function AboutPage() {
  return (
    <main className="flex flex-col items-center">
      <NavBar />
      <motion.section
        initial={{ opacity: 0, y: 50 }}
        whileInView={{ opacity: 1, y: 0 }}
        viewport={{ once: false }}
        transition={{ duration: 0.6 }}
        className="w-full max-w-6xl mx-auto px-4 pt-16 pb-12 text-center"
      >
        <div className="flex flex-col md:flex-row items-center">
          <div>
            <h1 className="text-4xl md:text-5xl font-bold mb-4">
              Hey! 👋
              <br />
              We&apos;re TryKatch
            </h1>
            <p className="text-lg text-gray-600 max-w-2xl mx-auto mb-12">
              We&apos;re building a new way for businesses to build their brands
              online and create authentic connections with their audience.
              We&apos;re also creating a new kind of company that&apos;s focused
              on the happiness of our customers and our team.
            </p>
          </div>

          <div className="relative h-[400px] w-full max-w-2xl mx-auto">
            {/* Diamond stack of images */}
            <motion.div
              animate={{ y: [0, -10, 0] }}
              transition={{ duration: 3, repeat: Infinity, ease: "easeInOut" }}
              className="absolute top-0 left-1/2 -translate-x-1/2 w-[300px] h-[200px] rotate-45 overflow-hidden rounded-3xl z-30"
            >
              <Image
                src="/placeholder.png"
                alt="Team photo 1"
                width={300}
                height={300}
                className="w-full h-full object-cover -rotate-45 scale-150"
              />
            </motion.div>
            <motion.div
              animate={{ y: [0, -10, 0] }}
              transition={{ duration: 3, repeat: Infinity, ease: "easeInOut" }}
              className="absolute top-[80px] left-1/2 -translate-x-1/2 w-[300px] h-[200px] rotate-45 overflow-hidden rounded-3xl z-20"
            >
              <Image
                src="/placeholder.png"
                alt="Team photo 2"
                width={300}
                height={300}
                className="w-full h-full object-cover -rotate-45 scale-150"
              />
            </motion.div>
            <div className="absolute top-[160px] left-1/2 -translate-x-1/2 w-[300px] h-[200px] rotate-45 overflow-hidden rounded-3xl z-10">
              <Image
                src="/placeholder.png"
                alt="Team photo 3"
                width={300}
                height={300}
                className="w-full h-full object-cover -rotate-45 scale-150"
              />
            </div>
          </div>
        </div>

        {/* Decorative curved line */}
        <div className="relative h-20 w-full">
          <svg
            className="absolute left-0 w-full"
            viewBox="0 0 500 50"
            preserveAspectRatio="none"
          >
            <path
              d="M0,25 C150,0 350,50 500,25 L500,50 L0,50 Z"
              fill="none"
              stroke="#E0E7FF"
              strokeWidth="2"
            />
          </svg>
        </div>
      </motion.section>

      {/* Our Story Section */}
      <section className="w-full max-w-6xl mx-auto px-4 py-12">
        <h2 className="text-3xl font-bold mb-8">Our Story</h2>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
          <div className="md:col-span-2">
            <p className="text-gray-600 mb-4">
              Trykatch was founded in 2010 as a simple way to schedule posts on
              Twitter. Today, we&apos;re a fully-remote team of 85+ people
              living and working in 15+ countries around the world.
            </p>
            <p className="text-gray-600 mb-4">
              Our mission is to provide essential tools to help small businesses
              authentically connect with their audience and grow their brand on
              social media. We&apos;re also passionate about challenging how
              businesses are run and showing that it&apos;s possible to
              prioritize team happiness and wellbeing while building a
              profitable business.
            </p>
            <p className="text-gray-600 mb-4">
              We&apos;ve been remote since day one, and we&apos;re committed to
              building a diverse, inclusive team. We believe in transparency and
              share everything from our salaries to our financials. We&apos;re
              also committed to building a sustainable business that&apos;s
              focused on long-term growth rather than short-term gains.
            </p>
            <p className="text-gray-600">
              We&apos;re proud to be a self-funded, profitable company
              that&apos;s growing sustainably. We&apos;re in this for the long
              haul, and we&apos;re excited to continue building a company that
              we&apos;re proud of.
            </p>
          </div>
          <div className="grid grid-cols-2 gap-4">
            <div className="rounded-lg overflow-hidden">
              <Image
                src="/placeholder.png"
                alt="Team photo"
                width={200}
                height={200}
                className="w-full h-full object-cover"
              />
            </div>
            <div className="rounded-lg overflow-hidden">
              <Image
                src="/placeholder.png"
                alt="Team photo"
                width={200}
                height={200}
                className="w-full h-full object-cover"
              />
            </div>
            <div className="col-span-2 rounded-lg overflow-hidden">
              <Image
                src="/placeholder.png"
                alt="Team photo"
                width={400}
                height={200}
                className="w-full h-full object-cover"
              />
            </div>
          </div>
        </div>
      </section>

      {/* Open Company Section */}
      <section className="w-full bg-gray-50 py-16">
        <div className="max-w-6xl mx-auto px-4">
          <h2 className="text-3xl font-bold mb-2">
            We are an <span className="text-blue-600">Open Company</span>
          </h2>
          <p className="text-gray-600 mb-12 max-w-2xl">
            We&apos;ve been transparent from the beginning. We share our
            journey, our financials, our salaries, and our decision-making
            process. We believe in building in public.
          </p>

          <div className="grid grid-cols-2 md:grid-cols-4 gap-6">
            <Card className="bg-blue-50 border-none">
              <CardContent className="p-6">
                {/* <h3 className="text-3xl font-bold text-blue-600">$1.76M</h3> */}
                <motion.h3
                  className="text-3xl font-bold text-blue-600"
                  initial={{ opacity: 0, y: 10 }}
                  whileInView={{ opacity: 1, y: 0 }}
                  viewport={{ once: true }}
                  transition={{ duration: 0.8 }}
                >
                  <motion.span
                    initial={{ opacity: 0 }}
                    animate={{ opacity: 1 }}
                    transition={{ duration: 1, delay: 0.5 }}
                  >
                    $1.76M
                  </motion.span>
                </motion.h3>
                <p className="text-gray-600">Monthly recurring revenue</p>
              </CardContent>
            </Card>
            <Card className="bg-purple-50 border-none">
              <CardContent className="p-6">
                <motion.h3
                  initial={{ opacity: 0, y: 10 }}
                  whileInView={{ opacity: 1, y: 0 }}
                  viewport={{ once: true }}
                  transition={{ duration: 0.8 }}
                  className="text-3xl font-bold text-purple-600"
                >
                  <motion.span
                    initial={{ opacity: 0 }}
                    animate={{ opacity: 1 }}
                    transition={{ duration: 1, delay: 0.5 }}
                  >
                    100,037
                  </motion.span>
                </motion.h3>
                <p className="text-gray-600">Paying customers</p>
              </CardContent>
            </Card>
            <Card className="bg-green-50 border-none">
              <CardContent className="p-6">
                <motion.h3
                  initial={{ opacity: 0, y: 10 }}
                  whileInView={{ opacity: 1, y: 0 }}
                  viewport={{ once: true }}
                  transition={{ duration: 0.8 }}
                  className="text-3xl font-bold text-green-600"
                >
                  <motion.span
                    initial={{ opacity: 0 }}
                    animate={{ opacity: 1 }}
                    transition={{ duration: 1, delay: 0.5 }}
                  >
                    $21.1M
                  </motion.span>
                </motion.h3>
                <p className="text-gray-600">Annual recurring revenue</p>
              </CardContent>
            </Card>
            <Card className="bg-yellow-50 border-none">
              <CardContent className="p-6">
                <motion.h3
                  initial={{ opacity: 0, y: 10 }}
                  whileInView={{ opacity: 1, y: 0 }}
                  viewport={{ once: true }}
                  transition={{ duration: 0.8 }}
                  className="text-3xl font-bold text-yellow-600"
                >
                  <motion.span
                    initial={{ opacity: 0 }}
                    animate={{ opacity: 1 }}
                    transition={{ duration: 1, delay: 0.5 }}
                  >
                    $27.85
                  </motion.span>
                </motion.h3>
                <p className="text-gray-600">Average revenue per user</p>
              </CardContent>
            </Card>
          </div>

          <div className="mt-8 text-center">
            <Button className="bg-blue-600 hover:bg-blue-700">
              View our open metrics
            </Button>
          </div>
        </div>
      </section>

      {/* Brief History Section */}
      <section className="w-full max-w-6xl mx-auto px-4 py-16">
        <h2 className="text-3xl font-bold mb-12">A brief history</h2>

        <div className="relative">
          {/* Timeline line */}
          <div className="absolute left-[15px] md:left-1/2 top-0 bottom-0 w-[2px] bg-gray-200 -ml-[1px]"></div>

          {/* Timeline items */}
          <div className="space-y-12">
            <div className="relative grid grid-cols-[30px_1fr] md:grid-cols-[1fr_30px_1fr] gap-4 md:gap-8">
              <div className="md:text-right md:pr-8">
                <h3 className="text-xl font-bold">2021</h3>
                <ul className="mt-2 space-y-2">
                  <li className="text-gray-600">Trykatch is founded</li>
                  <li className="text-gray-600">First paying customer</li>
                </ul>
              </div>
              <div className="flex items-start justify-center">
                <div className="w-[30px] h-[30px] rounded-full bg-blue-600 border-4 border-white z-10"></div>
              </div>
              <div className="hidden md:block"></div>
            </div>

            <div className="relative grid grid-cols-[30px_1fr] md:grid-cols-[1fr_30px_1fr] gap-4 md:gap-8">
              <div className="hidden md:block"></div>
              <div className="flex items-start justify-center">
                <div className="w-[30px] h-[30px] rounded-full bg-blue-600 border-4 border-white z-10"></div>
              </div>
              <div className="md:pl-8">
                <h3 className="text-xl font-bold">2022</h3>
                <ul className="mt-2 space-y-2">
                  <li className="text-gray-600">Raised Series A funding</li>
                  <li className="text-gray-600">Team grows to 10 people</li>
                </ul>
              </div>
            </div>

            <div className="relative grid grid-cols-[30px_1fr] md:grid-cols-[1fr_30px_1fr] gap-4 md:gap-8">
              <div className="md:text-right md:pr-8">
                <h3 className="text-xl font-bold">2023</h3>
                <ul className="mt-2 space-y-2">
                  <li className="text-gray-600">
                    Launched Trykatch for Business
                  </li>
                  <li className="text-gray-600">First company retreat</li>
                </ul>
              </div>
              <div className="flex items-start justify-center">
                <div className="w-[30px] h-[30px] rounded-full bg-blue-600 border-4 border-white z-10"></div>
              </div>
              <div className="hidden md:block"></div>
            </div>

            <div className="relative grid grid-cols-[30px_1fr] md:grid-cols-[1fr_30px_1fr] gap-4 md:gap-8">
              <div className="hidden md:block"></div>
              <div className="flex items-start justify-center">
                <div className="w-[30px] h-[30px] rounded-full bg-blue-600 border-4 border-white z-10"></div>
              </div>
              <div className="md:pl-8">
                <h3 className="text-xl font-bold">2024</h3>
                <ul className="mt-2 space-y-2">
                  <li className="text-gray-600">Reached $20M ARR</li>
                  <li className="text-gray-600">Team grows to 85+ people</li>
                  <li className="text-gray-600">
                    Launched new product features
                  </li>
                </ul>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Decorative curved line */}
      <div className="relative h-20 w-full">
        <svg
          className="absolute left-0 w-full"
          viewBox="0 0 500 50"
          preserveAspectRatio="none"
        >
          <path
            d="M0,25 C150,0 350,50 500,25 L500,50 L0,50 Z"
            fill="none"
            stroke="#E0E7FF"
            strokeWidth="2"
          />
        </svg>
      </div>

      {/* Team Photos Gallery */}
      <section className="w-full max-w-6xl mx-auto px-4 py-12">
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
          <div className="col-span-2 row-span-2">
            <Image
              src="/placeholder.png"
              alt="Team photo"
              width={400}
              height={400}
              className="w-full h-full object-cover rounded-lg"
            />
          </div>
          <div>
            <Image
              src="/placeholder.png"
              alt="Team photo"
              width={200}
              height={200}
              className="w-full h-full object-cover rounded-lg"
            />
          </div>
          <div>
            <Image
              src="/placeholder.png"
              alt="Team photo"
              width={200}
              height={200}
              className="w-full h-full object-cover rounded-lg"
            />
          </div>
          <div>
            <Image
              src="/placeholder.png"
              alt="Team photo"
              width={200}
              height={200}
              className="w-full h-full object-cover rounded-lg"
            />
          </div>
          <div>
            <Image
              src="/placeholder.png"
              alt="Team photo"
              width={200}
              height={200}
              className="w-full h-full object-cover rounded-lg"
            />
          </div>
        </div>
      </section>

      {/* Our Values Section */}
      <section className="w-full max-w-6xl mx-auto px-4 py-16">
        <h2 className="text-3xl font-bold mb-4">Our values</h2>
        <p className="text-gray-600 mb-12 max-w-2xl">
          Our values guide everything we do at Trykatch. They&apos;re the
          foundation of our company culture and the principles that drive our
          decision-making.
        </p>

        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
          <Card>
            <CardContent className="p-6">
              <div className="w-10 h-10 bg-orange-100 rounded-lg flex items-center justify-center mb-4">
                <span className="text-orange-600 text-xl">🔍</span>
              </div>
              <h3 className="text-lg font-bold mb-2">
                Default to transparency
              </h3>
              <p className="text-gray-600 mb-4">
                We believe in being open and honest with each other and with our
                customers. We share our journey, our financials, and our
                decision-making process.
              </p>
              <Link href="#" className="text-blue-600 hover:underline text-sm">
                Read more about transparency →
              </Link>
            </CardContent>
          </Card>

          <Card>
            <CardContent className="p-6">
              <div className="w-10 h-10 bg-blue-100 rounded-lg flex items-center justify-center mb-4">
                <span className="text-blue-600 text-xl">🌱</span>
              </div>
              <h3 className="text-lg font-bold mb-2">Improve consistently</h3>
              <p className="text-gray-600 mb-4">
                We believe in making small, consistent improvements every day.
                We&apos;re focused on long-term growth rather than short-term
                gains.
              </p>
              <Link href="#" className="text-blue-600 hover:underline text-sm">
                Read more about growth →
              </Link>
            </CardContent>
          </Card>

          <Card>
            <CardContent className="p-6">
              <div className="w-10 h-10 bg-purple-100 rounded-lg flex items-center justify-center mb-4">
                <span className="text-purple-600 text-xl">💜</span>
              </div>
              <h3 className="text-lg font-bold mb-2">Choose positivity</h3>
              <p className="text-gray-600 mb-4">
                We believe in approaching challenges with a positive mindset. We
                focus on solutions rather than problems and celebrate our wins
                along the way.
              </p>
              <Link href="#" className="text-blue-600 hover:underline text-sm">
                Read more about positivity →
              </Link>
            </CardContent>
          </Card>

          <Card>
            <CardContent className="p-6">
              <div className="w-10 h-10 bg-yellow-100 rounded-lg flex items-center justify-center mb-4">
                <span className="text-yellow-600 text-xl">🤝</span>
              </div>
              <h3 className="text-lg font-bold mb-2">Act with integrity</h3>
              <p className="text-gray-600 mb-4">
                We believe in doing the right thing, even when it&apos;s
                difficult. We&apos;re committed to building a company that
                we&apos;re proud of.
              </p>
              <Link href="#" className="text-blue-600 hover:underline text-sm">
                Read more about integrity →
              </Link>
            </CardContent>
          </Card>

          <Card>
            <CardContent className="p-6">
              <div className="w-10 h-10 bg-green-100 rounded-lg flex items-center justify-center mb-4">
                <span className="text-green-600 text-xl">🌍</span>
              </div>
              <h3 className="text-lg font-bold mb-2">Practice gratitude</h3>
              <p className="text-gray-600 mb-4">
                We believe in expressing gratitude for the opportunities we have
                and the people we work with. We&apos;re thankful for our
                customers and our team.
              </p>
              <Link href="#" className="text-blue-600 hover:underline text-sm">
                Read more about gratitude →
              </Link>
            </CardContent>
          </Card>

          <Card>
            <CardContent className="p-6">
              <div className="w-10 h-10 bg-red-100 rounded-lg flex items-center justify-center mb-4">
                <span className="text-red-600 text-xl">❤️</span>
              </div>
              <h3 className="text-lg font-bold mb-2">Show empathy</h3>
              <p className="text-gray-600 mb-4">
                We believe in understanding and sharing the feelings of others.
                We&apos;re committed to building a diverse, inclusive team.
              </p>
              <Link href="#" className="text-blue-600 hover:underline text-sm">
                Read more about empathy →
              </Link>
            </CardContent>
          </Card>
        </div>
      </section>

      {/* Team Members Section */}
      <section className="w-full max-w-6xl mx-auto px-4 py-16">
        <h2 className="text-3xl font-bold mb-4">Get to know our global team</h2>
        <div className="flex gap-4 mb-8">
          <Badge
            variant="outline"
            className="bg-blue-600 text-white hover:bg-blue-700"
          >
            All
          </Badge>
          <Badge variant="outline" className="hover:bg-gray-100">
            Leadership
          </Badge>
          <Badge variant="outline" className="hover:bg-gray-100">
            Engineering
          </Badge>
          <Badge variant="outline" className="hover:bg-gray-100">
            Marketing
          </Badge>
          <Badge variant="outline" className="hover:bg-gray-100">
            Customer Advocacy
          </Badge>
        </div>

        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4">
          {Array.from({ length: 5 }).map((_, i) => (
            <div key={i} className="group">
              <div className="rounded-lg overflow-hidden mb-2">
                <Image
                  src={`/placeholder.png`}
                  alt={`Team member ${i + 1}`}
                  width={150}
                  height={150}
                  className="w-full aspect-square object-cover group-hover:scale-105 transition-transform duration-300"
                />
              </div>
              <h3 className="font-medium text-sm">Team Member {i + 1}</h3>
              <p className="text-gray-600 text-xs">Role Title</p>
            </div>
          ))}
        </div>
      </section>

      {/* Join Us Section */}
      <section className="w-full max-w-6xl mx-auto px-4 py-16 text-center">
        <h2 className="text-3xl font-bold mb-4">Want to join us?</h2>
        <p className="text-gray-600 mb-8 max-w-xl mx-auto">
          We&apos;re always looking for talented people to join our team. Check
          out our open positions and apply today!
        </p>
        <Button className="bg-blue-600 hover:bg-blue-700">
          View open positions
        </Button>
      </section>

      {/* Where We Work Section */}
      <section className="w-full bg-blue-50 py-16">
        <div className="max-w-6xl mx-auto px-4 text-center">
          <h2 className="text-3xl font-bold mb-4">Where we work</h2>
          <p className="text-gray-600 mb-12 max-w-2xl mx-auto">
            We&apos;re a fully-remote team spread across the globe. We
            don&apos;t have an office, and we work from wherever we&apos;re
            happiest and most productive.
          </p>

          <div className="relative h-[300px] md:h-[400px] w-full max-w-2xl mx-auto">
            <Image
              src="/placeholder.png"
              alt="World map showing team distribution"
              width={600}
              height={400}
              className="w-full h-full object-contain"
            />

            {/* Stats */}
            <div className="absolute bottom-0 left-0 right-0 flex justify-center gap-8 text-center">
              <div>
                <h3 className="text-2xl font-bold">85</h3>
                <p className="text-sm text-gray-600">Team members</p>
              </div>
              <div>
                <h3 className="text-2xl font-bold">15</h3>
                <p className="text-sm text-gray-600">Countries</p>
              </div>
              <div>
                <h3 className="text-2xl font-bold">20</h3>
                <p className="text-sm text-gray-600">Timezones</p>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Thank You Section */}
      <section className="w-full max-w-6xl mx-auto px-4 py-16 text-center">
        <h2 className="text-2xl font-bold mb-4">Thanks for stopping by 👋</h2>
      </section>
      <SiteFooter />
    </main>
  );
}
