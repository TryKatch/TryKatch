"use client";

import Link from "next/link";
import Image from "next/image";
import { Facebook, Github, Instagram, Twitter, Youtube } from "lucide-react";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import { useState } from "react";

interface FooterSection {
  title: string;
  links: {
    title: string;
    href: string;
  }[];
}

const footerSections: FooterSection[] = [
  {
    title: "Solutions",
    links: [
      { title: "Marketing", href: "#" },
      { title: "Analytics", href: "#" },
      { title: "Automation", href: "#" },
      { title: "Commerce", href: "#" },
      { title: "Insights", href: "#" },
    ],
  },
  {
    title: "Support",
    links: [
      { title: "Submit ticket", href: "#" },
      { title: "Documentation", href: "#" },
      { title: "Guides", href: "#" },
    ],
  },
  {
    title: "Company",
    links: [
      { title: "About", href: "#" },
      { title: "Blog", href: "#" },
      { title: "Jobs", href: "#" },
      { title: "Press", href: "#" },
    ],
  },
  {
    title: "Legal",
    links: [
      { title: "Terms of service", href: "#" },
      { title: "Privacy policy", href: "#" },
      { title: "License", href: "#" },
    ],
  },
];

const socialLinks = [
  { icon: Facebook, href: "#" },
  { icon: Instagram, href: "#" },
  { icon: Twitter, href: "#" },
  { icon: Github, href: "#" },
  { icon: Youtube, href: "#" },
];

export function SiteFooter() {
  const [openSections, setOpenSections] = useState<string[]>([]);

  const toggleSection = (title: string) => {
    setOpenSections((current) =>
      current.includes(title)
        ? current.filter((t) => t !== title)
        : [...current, title]
    );
  };

  return (
    <footer className="w-full bg-[#0F0B1B] text-gray-400">
      <div className="container mx-auto px-4 py-12">
        <div className="grid gap-8 lg:grid-cols-6">
          {/* Logo and social section */}
          <div className="lg:col-span-2">
            <Link href="/" className="mb-6 inline-block">
              <Image
                src="/favicon.ico"
                alt="Logo"
                width={100}
                height={50}
                className="w-[200px] h-[90px]"
              />
            </Link>
            <p className="mb-6 text-sm">
              Making the world a better place through constructing elegant
              hierarchies.
            </p>
            <div className="flex space-x-4">
              {socialLinks.map((social, index) => {
                const Icon = social.icon;
                return (
                  <Link
                    key={index}
                    href={social.href}
                    className="text-gray-400 transition-colors hover:text-gray-300"
                  >
                    <Icon className="h-5 w-5" />
                  </Link>
                );
              })}
            </div>
          </div>

          {/* Footer sections */}
          <div className="lg:col-span-4">
            <div className="grid gap-8 sm:grid-cols-2 lg:grid-cols-4">
              {footerSections.map((section) => (
                <div key={section.title}>
                  {/* Desktop version */}
                  <div className="hidden lg:block">
                    <h3 className="mb-4 text-sm font-semibold text-white">
                      {section.title}
                    </h3>
                    <ul className="space-y-3">
                      {section.links.map((link) => (
                        <li key={link.title}>
                          <Link
                            href={link.href}
                            className="text-sm transition-colors hover:text-gray-300"
                          >
                            {link.title}
                          </Link>
                        </li>
                      ))}
                    </ul>
                  </div>

                  {/* Mobile version */}
                  <div className="lg:hidden">
                    <Collapsible
                      open={openSections.includes(section.title)}
                      onOpenChange={() => toggleSection(section.title)}
                    >
                      <CollapsibleTrigger className="flex w-full items-center justify-between py-2 text-sm font-semibold text-white">
                        {section.title}
                        <span
                          className={`transform transition-transform ${
                            openSections.includes(section.title)
                              ? "rotate-180"
                              : ""
                          }`}
                        >
                          ▼
                        </span>
                      </CollapsibleTrigger>
                      <CollapsibleContent>
                        <ul className="space-y-3 py-3">
                          {section.links.map((link) => (
                            <li key={link.title}>
                              <Link
                                href={link.href}
                                className="text-sm transition-colors hover:text-gray-300"
                              >
                                {link.title}
                              </Link>
                            </li>
                          ))}
                        </ul>
                      </CollapsibleContent>
                    </Collapsible>
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>

        {/* Copyright */}
        <div className="mt-12 border-t border-gray-800 pt-8 text-sm">
          © {new Date().getFullYear()} TryKatch, Inc. All rights reserved.
        </div>
      </div>
    </footer>
  );
}
