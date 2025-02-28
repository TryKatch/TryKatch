"use client";
import { useState } from "react";
import Image from "next/image";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { motion, AnimatePresence } from "framer-motion";
import { Menu } from "lucide-react";
import { Sheet, SheetContent, SheetTrigger } from "@/components/ui/sheet";
import { Button } from "@/components/ui/button";

const navigation = [
  { name: "Home", href: "/" },
  { name: "About", href: "/about" },
  { name: "Service", href: "/service" },
  { name: "Blog", href: "/blog" },
  { name: "Team", href: "/team" },
  { name: "Career", href: "/career" },
];

export function NavBar() {
  const [isOpen, setIsOpen] = useState(false);
  const pathname = usePathname();

  return (
    <header className="sticky top-0 z-50 w-full border-b bg-white">
      <div className="container mx-auto flex h-16 items-center justify-between px-4">
        {/* Logo */}
        <Link href="/" className="flex items-center space-x-2">
          <Image
            src="/favicon.ico"
            alt="Logo"
            width={100}
            height={60}
            className="w-[100px] h-[60px]"
          />
        </Link>

        {/* Desktop Navigation */}
        <nav className="hidden items-center space-x-8 md:flex">
          {navigation.map((item) => (
            <Link
              key={item.name}
              href={item.href}
              className="relative text-sm font-medium text-gray-700 transition-colors hover:text-gray-900"
            >
              {item.name}
              {pathname === item.href && (
                <motion.div
                  layoutId="underline"
                  className="absolute -bottom-[21px] h-[2px] w-full bg-blue-600"
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  transition={{
                    type: "spring",
                    stiffness: 380,
                    damping: 30,
                  }}
                />
              )}
            </Link>
          ))}
          {/* Contact Button - Desktop */}
          <div className="hidden md:block">
            <Button asChild>
              <Link href="#contact">Contact</Link>
            </Button>
          </div>
        </nav>

        {/* Mobile Menu */}
        <Sheet open={isOpen} onOpenChange={setIsOpen}>
          <SheetTrigger asChild className="md:hidden">
            <Button variant="ghost" size="icon">
              <Menu className="h-6 w-6" />
              <span className="sr-only">Toggle menu</span>
            </Button>
          </SheetTrigger>
          <SheetContent side="right" className="w-[300px] sm:w-[400px]">
            <div className="flex h-full flex-col">
              {/* <div className="flex items-center justify-between border-b px-4 py-4">
                <span className="text-lg font-semibold">Menu</span>
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => setIsOpen(false)}
                >
                  <X className="h-6 w-6" />
                </Button>
              </div> */}
              <nav className="flex-1 space-y-2 overflow-y-auto p-4">
                <AnimatePresence>
                  {navigation.map((item) => (
                    <motion.div
                      key={item.name}
                      initial={{ opacity: 0, x: -20 }}
                      animate={{ opacity: 1, x: 0 }}
                      exit={{ opacity: 0, x: 20 }}
                      transition={{
                        type: "spring",
                        stiffness: 380,
                        damping: 30,
                      }}
                    >
                      <Link
                        href={item.href}
                        className={`block w-full rounded-lg px-4 py-2 text-sm font-medium transition-colors hover:bg-gray-100 ${
                          pathname === item.href
                            ? "bg-gray-100 text-blue-600"
                            : "text-gray-700"
                        }`}
                        onClick={() => setIsOpen(false)}
                      >
                        {item.name}
                      </Link>
                    </motion.div>
                  ))}
                </AnimatePresence>
              </nav>
              <div className="border-t p-4">
                <Button
                  asChild
                  className="w-full"
                  onClick={() => setIsOpen(false)}
                >
                  <Link href="/contact">Contact</Link>
                </Button>
              </div>
            </div>
          </SheetContent>
        </Sheet>
      </div>
    </header>
  );
}
