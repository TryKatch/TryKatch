"use client";

import { useEffect, useState } from "react";
import axios from "axios";
import Link from "next/link";
import Image from "next/image";
import { Badge } from "@/components/ui/badge";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Search, Calendar, Clock, ArrowRight, User } from "lucide-react";
import { motion } from "framer-motion";

// type BlogPost = {
//   id: string;
//   documentId: string;
//   title: string;
//   slug: string;
//   content: any[]; // Content is an array of objects
//   published: string;
// };

// Types for our blog data
interface Author {
  name: string;
  role: string;
  avatar?: string;
}

interface BlogPost {
  id: string;
  title: string;
  description: string;
  image: string;
  date: string;
  readTime: string;
  category: string;
  author: Author;
  featured?: boolean;
}

// Sample blog posts data
const samplePosts: BlogPost[] = [
  {
    id: "webinar-trykatch",
    title: "How to Run a Webinar with TryKatch: A Complete Step-by-Step Guide",
    description:
      "This complete guide shows you how you can plan, schedule, host, record, and repurpose your webinar with just one platform; TryKatch!",
    image: "/placeholder.png",
    date: "January 23, 2025",
    readTime: "20 min",
    category: "Webinar",
    author: {
      name: "Kendall Breitman",
      role: "Social Media & Community Expert",
      avatar: "/placeholder.png",
    },
    featured: true,
  },
  {
    id: "modern-web-development",
    title: "Modern Web Development Trends in 2025",
    description:
      "Explore the latest trends in web development including AI integration, serverless architecture, and progressive web apps.",
    image: "/placeholder.png",
    date: "January 20, 2025",
    readTime: "15 min",
    category: "Development",
    author: {
      name: "Sarah Johnson",
      role: "Senior Developer",
      avatar: "/placeholder.png",
    },
  },
  {
    id: "ui-ux-best-practices",
    title: "UI/UX Best Practices for Better User Experience",
    description:
      "Learn essential UI/UX principles that will help you create more engaging and user-friendly interfaces.",
    image: "/placeholder.png",
    date: "January 18, 2025",
    readTime: "12 min",
    category: "Design",
    author: {
      name: "Michael Chen",
      role: "UX Designer",
      avatar: "/placeholder.png",
    },
  },
  {
    id: "cloud-computing-guide",
    title: "Complete Guide to Cloud Computing for Businesses",
    description:
      "Everything you need to know about migrating your business to the cloud and choosing the right cloud services.",
    image: "/placeholder.png",
    date: "January 15, 2025",
    readTime: "18 min",
    category: "Cloud",
    author: {
      name: "David Wilson",
      role: "Cloud Architect",
      avatar: "/placeholder.png",
    },
  },
];

const categories = ["All", "Development", "Design", "Webinar", "Cloud", "Business"];

export default function BlogList() {
  const [posts, setPosts] = useState<BlogPost[]>(samplePosts);
  const [filteredPosts, setFilteredPosts] = useState<BlogPost[]>(samplePosts);
  const [searchTerm, setSearchTerm] = useState("");
  const [selectedCategory, setSelectedCategory] = useState("All");
  const [loading, setLoading] = useState(false);

  // Filter posts based on search and category
  useEffect(() => {
    let filtered = posts;

    if (selectedCategory !== "All") {
      filtered = filtered.filter(post => post.category === selectedCategory);
    }

    if (searchTerm) {
      filtered = filtered.filter(post =>
        post.title.toLowerCase().includes(searchTerm.toLowerCase()) ||
        post.description.toLowerCase().includes(searchTerm.toLowerCase())
      );
    }

    setFilteredPosts(filtered);
  }, [posts, searchTerm, selectedCategory]);

  const featuredPost = posts.find(post => post.featured);
  const regularPosts = filteredPosts.filter(post => !post.featured);

  return (
    <div className="min-h-screen bg-gradient-to-br from-background via-accent/30 to-accent/50">
      <div className="container mx-auto px-4 py-16 md:py-20">
        {/* Header */}
        <motion.div
          initial={{ opacity: 0, y: 30 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.8 }}
          className="text-center mb-16"
        >
          <div className="inline-flex items-center gap-2 px-4 py-2 bg-accent border border-border rounded-full text-sm font-medium text-accent-foreground mb-6">
            <Calendar className="w-4 h-4" />
            Our Blog
          </div>
          
          <h1 className="text-4xl md:text-5xl lg:text-6xl font-bold text-foreground mb-6">
            Latest Insights &
            <br />
            <span className="text-brand-gradient">Tech Stories</span>
          </h1>
          
          <p className="text-lg md:text-xl text-muted-foreground max-w-3xl mx-auto leading-relaxed">
            Stay updated with the latest trends in technology, development, and digital innovation. 
            Our experts share insights to help you stay ahead in the digital world.
          </p>
        </motion.div>

        {/* Search and Filter */}
        <motion.div
          initial={{ opacity: 0, y: 20 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.6, delay: 0.2 }}
          className="mb-12"
        >
          <div className="flex flex-col md:flex-row gap-4 items-center justify-between">
            {/* Search */}
            <div className="relative w-full md:w-96">
              <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 text-muted-foreground w-5 h-5" />
              <Input
                type="text"
                placeholder="Search articles..."
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                className="pl-10 h-12 border-border focus:border-blue-500"
              />
            </div>

            {/* Category Filter */}
            <div className="flex flex-wrap gap-2">
              {categories.map((category) => (
                <Button
                  key={category}
                  variant={selectedCategory === category ? "default" : "outline"}
                  size="sm"
                  onClick={() => setSelectedCategory(category)}
                  className={`${
                    selectedCategory === category
                      ? "brand-gradient text-white"
                      : "border-border text-foreground hover:bg-accent"
                  }`}
                >
                  {category}
                </Button>
              ))}
            </div>
          </div>
        </motion.div>

        {/* Featured Post */}
        {featuredPost && (
          <motion.div
            initial={{ opacity: 0, y: 30 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.8, delay: 0.3 }}
            className="mb-16"
          >
            <div className="bg-card/80 backdrop-blur-sm rounded-3xl shadow-xl border border-border overflow-hidden hover-lift">
              <div className="grid md:grid-cols-2 gap-8 p-8 md:p-12">
                <div className="space-y-6">
                  <div className="flex items-center gap-4">
                    <Badge className="bg-gradient-to-r from-blue-600 to-purple-600 text-white">
                      Featured
                    </Badge>
                    <Badge variant="outline" className="border-border text-foreground">
                      {featuredPost.category}
                    </Badge>
                  </div>
                  
                  <h2 className="text-2xl md:text-3xl lg:text-4xl font-bold text-card-foreground leading-tight">
                    {featuredPost.title}
                  </h2>
                  
                  <p className="text-lg text-muted-foreground leading-relaxed">
                    {featuredPost.description}
                  </p>

                  <div className="flex items-center gap-4 pt-4">
                    <Avatar className="w-12 h-12">
                      <AvatarImage src={featuredPost.author.avatar} alt={featuredPost.author.name} />
                      <AvatarFallback>
                        <User className="w-6 h-6" />
                      </AvatarFallback>
                    </Avatar>
                    <div>
                      <p className="font-semibold text-card-foreground">{featuredPost.author.name}</p>
                      <p className="text-sm text-muted-foreground">{featuredPost.author.role}</p>
                    </div>
                    <div className="ml-auto flex items-center gap-4 text-sm text-muted-foreground">
                      <div className="flex items-center gap-1">
                        <Calendar className="w-4 h-4" />
                        {featuredPost.date}
                      </div>
                      <div className="flex items-center gap-1">
                        <Clock className="w-4 h-4" />
                        {featuredPost.readTime}
                      </div>
                    </div>
                  </div>

                  <Button asChild className="brand-gradient text-white hover:opacity-90 shadow-lg hover:shadow-xl group">
                    <Link href={`/blog/${featuredPost.id}`}>
                      Read Full Article
                      <ArrowRight className="ml-2 w-4 h-4 transition-transform group-hover:translate-x-1" />
                    </Link>
                  </Button>
                </div>
                
                <div className="relative aspect-[4/3] rounded-2xl overflow-hidden">
                  <Image
                    src={featuredPost.image}
                    alt={featuredPost.title}
                    fill
                    className="object-cover"
                  />
                </div>
              </div>
            </div>
          </motion.div>
        )}

        {/* Regular Posts Grid */}
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-8">
          {regularPosts.map((post, index) => (
            <motion.article
              key={post.id}
              initial={{ opacity: 0, y: 30 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.6, delay: 0.1 * index }}
              className="bg-card/80 backdrop-blur-sm rounded-2xl shadow-lg border border-border overflow-hidden hover-lift group"
            >
              <div className="relative aspect-[16/10] overflow-hidden">
                <Image
                  src={post.image}
                  alt={post.title}
                  fill
                  className="object-cover transition-transform duration-300 group-hover:scale-105"
                />
                <div className="absolute top-4 left-4">
                  <Badge variant="secondary" className="bg-background/90 text-foreground">
                    {post.category}
                  </Badge>
                </div>
              </div>
              
              <div className="p-6 space-y-4">
                <h3 className="text-xl font-bold text-card-foreground leading-tight group-hover:text-blue-600 transition-colors">
                  <Link href={`/blog/${post.id}`}>
                    {post.title}
                  </Link>
                </h3>
                
                <p className="text-muted-foreground leading-relaxed line-clamp-3">
                  {post.description}
                </p>

                <div className="flex items-center gap-3 pt-4 border-t border-border">
                  <Avatar className="w-8 h-8">
                    <AvatarImage src={post.author.avatar} alt={post.author.name} />
                    <AvatarFallback>
                      <User className="w-4 h-4" />
                    </AvatarFallback>
                  </Avatar>
                  <div className="flex-1">
                    <p className="text-sm font-medium text-card-foreground">{post.author.name}</p>
                    <div className="flex items-center gap-3 text-xs text-muted-foreground">
                      <span>{post.date}</span>
                      <span>•</span>
                      <span>{post.readTime}</span>
                    </div>
                  </div>
                  <Button variant="ghost" size="sm" asChild>
                    <Link href={`/blog/${post.id}`}>
                      <ArrowRight className="w-4 h-4" />
                    </Link>
                  </Button>
                </div>
              </div>
            </motion.article>
          ))}
        </div>

        {/* No Results */}
        {filteredPosts.length === 0 && (
          <motion.div
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            className="text-center py-16"
          >
            <div className="max-w-md mx-auto">
              <Search className="w-16 h-16 text-muted-foreground mx-auto mb-4" />
              <h3 className="text-xl font-semibold text-foreground mb-2">No articles found</h3>
              <p className="text-muted-foreground mb-6">
                Try adjusting your search terms or browse different categories.
              </p>
              <Button 
                onClick={() => {
                  setSearchTerm("");
                  setSelectedCategory("All");
                }}
                variant="outline"
              >
                Clear Filters
              </Button>
            </div>
          </motion.div>
        )}

        {/* Load More Button */}
        {regularPosts.length > 0 && (
          <motion.div
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            transition={{ delay: 0.5 }}
            className="text-center mt-12"
          >
            <Button 
              variant="outline" 
              size="lg"
              className="border-border text-foreground hover:bg-accent px-8 py-6"
            >
              Load More Articles
            </Button>
          </motion.div>
        )}
      </div>
    </div>
  );
}
