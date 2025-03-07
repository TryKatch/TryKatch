"use client";

import { useState } from "react";
import { MapPin, Mail, Phone, Send } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { Textarea } from "@/components/ui/textarea";
// import { toast } from "@/components/ui/use-toast";
import { toast } from "sonner";

export default function ContactSection() {
  const [formData, setFormData] = useState({
    name: "",
    email: "",
    phone: "",
    preferredMethod: "email",
    message: "",
  });

  const handleChange = (e: any) => {
    const { name, value } = e.target;
    setFormData((prev) => ({ ...prev, [name]: value }));
  };

  const handleRadioChange = (value: any) => {
    setFormData((prev) => ({ ...prev, preferredMethod: value }));
  };

  const handleSubmit = (e: any) => {
    e.preventDefault();
    console.log("Form submitted:", formData);
    toast(<div>We&apos;ll get back to you as soon as possible</div>);
    // toast({
    //   title: "Message sent!",
    //   description: "We'll get back to you as soon as possible.",
    // });
    // Reset form
    setFormData({
      name: "",
      email: "",
      phone: "",
      preferredMethod: "email",
      message: "",
    });
  };

  return (
    <section className="md:w-[90%] m-auto py-12 md:py-24">
      <div className="container px-4 md:px-6">
        <div className="grid gap-8 lg:grid-cols-2 lg:gap-12">
          {/* Contact Information */}
          <div className="flex flex-col justify-center space-y-8">
            <div>
              <h2 className="text-3xl font-bold tracking-tighter text-[#2a3990] md:text-4xl mb-6">
                Contact us
              </h2>
              <div className="space-y-4 text-slate-700">
                <div className="flex items-start space-x-3">
                  <MapPin className="h-5 w-5 text-[#2a3990] mt-1 flex-shrink-0" />
                  <p className="text-base">
                    KN 78 St, Norrsken, Kigali City, Rwanda
                  </p>
                </div>
                <div className="flex items-center space-x-3">
                  <Mail className="h-5 w-5 text-[#2a3990] flex-shrink-0" />
                  <a
                    href="mailto:contact@trykatch.com"
                    className="text-base hover:text-[#2a3990] transition-colors"
                  >
                    contact@trykatch.com
                  </a>
                </div>
                <div className="flex items-center space-x-3">
                  <Phone className="h-5 w-5 text-[#2a3990] flex-shrink-0" />
                  <a
                    href="tel:+250790182885"
                    className="text-base hover:text-[#2a3990] transition-colors"
                  >
                    +250 790 182 885
                  </a>
                </div>
              </div>
            </div>

            {/* Map or Additional Info */}
            {/* <div className="rounded-lg border bg-slate-50 p-4 shadow-sm">
              <h3 className="text-lg font-medium mb-2">Business Hours</h3>
              <div className="space-y-1 text-sm">
                <p>Monday - Friday: 8:00 AM - 6:00 PM</p>
                <p>Saturday: 9:00 AM - 1:00 PM</p>
                <p>Sunday: Closed</p>
              </div>
            </div> */}
          </div>

          {/* Contact Form */}
          <div className="rounded-xl overflow-hidden shadow-lg">
            <form
              onSubmit={handleSubmit}
              className="bg-gradient-to-b from-[#2a3990] to-[#8ca9e0] p-6 md:p-8"
            >
              <div className="mb-6">
                <h2 className="text-2xl font-bold text-white mb-6">
                  Send us a Message
                </h2>

                <div className="space-y-4">
                  <div className="space-y-2">
                    <Label htmlFor="name" className="sr-only">
                      Name
                    </Label>
                    <Input
                      id="name"
                      name="name"
                      placeholder="Name"
                      value={formData.name}
                      onChange={handleChange}
                      required
                      className="bg-white/10 border-white/20 text-white placeholder:text-white/70 focus-visible:ring-white"
                    />
                  </div>

                  <div className="space-y-2">
                    <Label htmlFor="email" className="sr-only">
                      Email
                    </Label>
                    <Input
                      id="email"
                      name="email"
                      type="email"
                      placeholder="Email"
                      value={formData.email}
                      onChange={handleChange}
                      required
                      className="bg-white/10 border-white/20 text-white placeholder:text-white/70 focus-visible:ring-white"
                    />
                  </div>

                  <div className="space-y-2">
                    <Label htmlFor="phone" className="sr-only">
                      Phone
                    </Label>
                    <Input
                      id="phone"
                      name="phone"
                      type="tel"
                      placeholder="Phone"
                      value={formData.phone}
                      onChange={handleChange}
                      className="bg-white/10 border-white/20 text-white placeholder:text-white/70 focus-visible:ring-white"
                    />
                  </div>

                  <div className="space-y-2">
                    <p className="text-sm font-medium text-white">
                      Preferred method of communication
                    </p>
                    <RadioGroup
                      value={formData.preferredMethod}
                      onValueChange={handleRadioChange}
                      className="flex space-x-8"
                    >
                      <div className="flex items-center space-x-2">
                        <RadioGroupItem
                          value="email"
                          id="email-radio"
                          className="border-white text-white"
                        />
                        <Label htmlFor="email-radio" className="text-white">
                          Email
                        </Label>
                      </div>
                      <div className="flex items-center space-x-2">
                        <RadioGroupItem
                          value="phone"
                          id="phone-radio"
                          className="border-white text-white"
                        />
                        <Label htmlFor="phone-radio" className="text-white">
                          Phone
                        </Label>
                      </div>
                    </RadioGroup>
                  </div>

                  <div className="space-y-2">
                    <Label htmlFor="message" className="sr-only">
                      Message
                    </Label>
                    <Textarea
                      id="message"
                      name="message"
                      placeholder="Message"
                      value={formData.message}
                      onChange={handleChange}
                      required
                      className="min-h-[120px] bg-white/10 border-white/20 text-white placeholder:text-white/70 focus-visible:ring-white"
                    />
                  </div>
                </div>
              </div>

              <Button
                type="submit"
                className="w-full bg-white text-[#2a3990] hover:bg-white/90 hover:text-[#2a3990]/90 transition-colors"
              >
                <Send className="mr-2 h-4 w-4" />
                Submit
              </Button>
            </form>
          </div>
        </div>
      </div>
    </section>
  );
}
