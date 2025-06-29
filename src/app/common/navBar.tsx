"use client";
import { useState, useEffect } from "react";
import Image from "next/image";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { motion, AnimatePresence } from "framer-motion";
import { Menu, X } from "lucide-react";
import { Sheet, SheetContent, SheetTrigger } from "@/components/ui/sheet";
import { Button } from "@/components/ui/button";
import { ThemeToggle } from "@/components/ui/theme-toggle";

const navigation = [
  { name: "Home", href: "/" },
  { name: "About", href: "/about" },
  { name: "Services", href: "/#service" },
  { name: "Portfolio", href: "/portfolio" },
  { name: "Blog", href: "/blog" },
  { name: "Career", href: "/career" },
];

export function NavBar() {
  const [isOpen, setIsOpen] = useState(false);
  const [scrolled, setScrolled] = useState(false);
  const pathname = usePathname();

  useEffect(() => {
    const handleScroll = () => {
      setScrolled(window.scrollY > 20);
    };

    window.addEventListener("scroll", handleScroll);
    return () => window.removeEventListener("scroll", handleScroll);
  }, []);

  return (
    <header 
      className={`fixed top-0 z-50 w-full transition-all duration-300 ${
        scrolled 
          ? "bg-background/95 backdrop-blur-md shadow-lg border-b border-border/50" 
          : "bg-background/80 backdrop-blur-sm border-b border-transparent"
      }`}
    >
      <div className="container mx-auto flex h-16 lg:h-20 items-center justify-between px-4 lg:px-6">
        {/* Logo */}
        <Link href="/" className="flex items-center space-x-2 group">
          <div className="relative overflow-hidden rounded-lg">
            <Image
              src="/favicon.ico"
              alt="TryKatch Logo"
              width={120}
              height={70}
              className="w-[120px] h-[70px] transition-transform duration-300 group-hover:scale-105"
              priority
            />
          </div>
        </Link>

        {/* Desktop Navigation */}
        <nav className="hidden lg:flex items-center space-x-1">
          {navigation.map((item) => (
            <Link
              key={item.name}
              href={item.href}
              className="relative px-4 py-2 text-sm font-medium text-foreground/70 transition-all duration-200 hover:text-foreground rounded-lg hover:bg-accent"
            >
              {item.name}
              {pathname === item.href && (
                <motion.div
                  layoutId="navbar-underline"
                  className="absolute bottom-0 left-1/2 h-0.5 w-8 -translate-x-1/2 bg-gradient-to-r from-blue-600 to-purple-600 rounded-full"
                  initial={{ opacity: 0, scale: 0.8 }}
                  animate={{ opacity: 1, scale: 1 }}
                  transition={{
                    type: "spring",
                    stiffness: 380,
                    damping: 30,
                  }}
                />
              )}
            </Link>
          ))}
        </nav>

        {/* Desktop CTA & Theme Toggle */}
        <div className="hidden lg:flex items-center space-x-3">
          <ThemeToggle />
          <Button 
            variant="outline" 
            className="text-foreground/70 hover:text-foreground hover:bg-accent border-border"
            asChild
          >
            <Link href="#contact">Get Quote</Link>
          </Button>
          <Button 
            className="brand-gradient text-white hover:opacity-90 shadow-lg hover:shadow-xl transition-all duration-300"
            asChild
          >
            <Link href="#contact">Start Project</Link>
          </Button>
        </div>

        {/* Mobile Menu */}
        <div className="lg:hidden flex items-center space-x-2">
          <ThemeToggle />
          <Sheet open={isOpen} onOpenChange={setIsOpen}>
            <SheetTrigger asChild>
              <Button 
                variant="ghost" 
                size="icon"
                className="relative w-10 h-10 text-foreground/70 hover:text-foreground hover:bg-accent"
              >
                <AnimatePresence mode="wait">
                  {isOpen ? (
                    <motion.div
                      key="close"
                      initial={{ rotate: -90, opacity: 0 }}
                      animate={{ rotate: 0, opacity: 1 }}
                      exit={{ rotate: 90, opacity: 0 }}
                      transition={{ duration: 0.2 }}
                    >
                      <X className="h-5 w-5" />
                    </motion.div>
                  ) : (
                    <motion.div
                      key="menu"
                      initial={{ rotate: 90, opacity: 0 }}
                      animate={{ rotate: 0, opacity: 1 }}
                      exit={{ rotate: -90, opacity: 0 }}
                      transition={{ duration: 0.2 }}
                    >
                      <Menu className="h-5 w-5" />
                    </motion.div>
                  )}
                </AnimatePresence>
                <span className="sr-only">Toggle menu</span>
              </Button>
            </SheetTrigger>
            
            <SheetContent 
              side="right" 
              className="w-[300px] sm:w-[400px] bg-background/95 backdrop-blur-md border-l border-border"
            >
              <div className="flex h-full flex-col">
                {/* Mobile Logo */}
                <div className="flex items-center justify-between border-b border-border pb-4 mb-6">
                  <Link href="/" onClick={() => setIsOpen(false)}>
                    <Image
                      src="/favicon.ico"
                      alt="TryKatch Logo"
                      width={100}
                      height={60}
                      className="w-[100px] h-[60px]"
                    />
                  </Link>
                </div>

                {/* Mobile Navigation */}
                <nav className="flex-1 space-y-2 overflow-y-auto">
                  <AnimatePresence>
                    {navigation.map((item, index) => (
                      <motion.div
                        key={item.name}
                        initial={{ opacity: 0, x: -20 }}
                        animate={{ opacity: 1, x: 0 }}
                        exit={{ opacity: 0, x: 20 }}
                        transition={{
                          type: "spring",
                          stiffness: 380,
                          damping: 30,
                          delay: index * 0.05,
                        }}
                      >
                        <Link
                          href={item.href}
                          className={`flex items-center w-full rounded-xl px-4 py-3 text-base font-medium transition-all duration-200 ${
                            pathname === item.href
                              ? "bg-accent text-foreground border border-border"
                              : "text-foreground/70 hover:bg-accent hover:text-foreground"
                          }`}
                          onClick={() => setIsOpen(false)}
                        >
                          {item.name}
                          {pathname === item.href && (
                            <motion.div
                              className="ml-auto w-2 h-2 bg-blue-600 rounded-full"
                              initial={{ scale: 0 }}
                              animate={{ scale: 1 }}
                              transition={{ delay: 0.1 }}
                            />
                          )}
                        </Link>
                      </motion.div>
                    ))}
                  </AnimatePresence>
                </nav>

                {/* Mobile CTA */}
                <motion.div 
                  className="border-t border-border pt-6 space-y-3"
                  initial={{ opacity: 0, y: 20 }}
                  animate={{ opacity: 1, y: 0 }}
                  transition={{ delay: 0.3 }}
                >
                  <Button
                    variant="outline"
                    className="w-full border-border text-foreground/70 hover:bg-accent"
                    onClick={() => setIsOpen(false)}
                    asChild
                  >
                    <Link href="#contact">Get Quote</Link>
                  </Button>
                  <Button
                    className="w-full brand-gradient text-white hover:opacity-90 shadow-lg"
                    onClick={() => setIsOpen(false)}
                    asChild
                  >
                    <Link href="#contact">Start Project</Link>
                  </Button>
                </motion.div>
              </div>
            </SheetContent>
          </Sheet>
        </div>
      </div>
    </header>
  );
}
