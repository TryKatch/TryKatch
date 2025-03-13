"use client";

import { useEffect, useState } from "react";
import axios from "axios";
import Link from "next/link";

import Image from "next/image";
import { Badge } from "@/components/ui/badge";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

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
  avatar: string;
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
}

// Featured post data
const featuredPost: BlogPost = {
  id: "webinar-TryKatch",
  title: "How to Run a Webinar with TryKatch: A Complete Step-by-Step Guide",
  description:
    "This complete guide shows you how you can plan, schedule, host, recording, and repurpose your webinar with just one platform; TryKatch!",
  image: "/placeholder.png",
  date: "January 23, 2025",
  readTime: "20 min",
  category: "Webinar",
  author: {
    name: "Kendall Breitman",
    role: "Social Media & Community Expert",
    avatar: "/placeholder.png",
  },
};

// Trending posts data
const trendingPosts: BlogPost[] = [
  {
    id: "video-podcast-2025",
    title: "How to Record a Video Podcast in 2025 (5 Easy Methods)",
    description:
      "Learn the best ways to record high-quality video podcasts in 2025",
    image: "/placeholder.png",
    date: "Jan 25, 2025",
    readTime: "14 min",
    category: "Video podcast",
    author: {
      name: "Stephen Robles",
      role: "Video & Podcast Creator",
      avatar: "/placeholder.png",
    },
  },
  {
    id: "zoom-video-quality",
    title: "How to Improve Zoom Video Quality (A Step-by-Step Guide)",
    description: "Enhance your Zoom calls with these simple techniques",
    image: "/placeholder.png",
    date: "Jan 10, 2025",
    readTime: "10 min",
    category: "Recording software",
    author: {
      name: "Stephen Robles",
      role: "Video & Podcast Creator",
      avatar: "/placeholder.png",
    },
  },
  {
    id: "iphone-webcam",
    title: "How to Use iPhone as Webcam on Mac & Windows | Step-by-Step Guide",
    description:
      "Turn your iPhone into a high-quality webcam for your computer",
    image: "/placeholder.png",
    date: "Jun 28, 2024",
    readTime: "14 min",
    category: "Studio equipment",
    author: {
      name: "Kendall Breitman",
      role: "Social Media & Community Expert",
      avatar: "/placeholder.png",
    },
  },
];

// Popular posts data
const popularPosts: BlogPost[] = [
  {
    id: "podcast-equipment-2025",
    title: "Best Podcast Equipment for Beginners & Pros in 2025 - All Budgets",
    description:
      "Discover the best podcast equipment for a pro or beginner setup. We share considerations and recommendations for mics, cameras, and more.",
    image: "/placeholder.png",
    date: "January 24, 2025",
    readTime: "9 min",
    category: "Podcast equipment",
    author: {
      name: "Stephen Robles",
      role: "Video & Podcast Creator",
      avatar: "/placeholder.png",
    },
  },
  {
    id: "podcast-recording-software",
    title: "21 Best Podcast Recording Software for Pros & Beginners | 2025",
    description:
      "Looking for top-quality podcast software? Check out our list of 15 of the best podcast recording software. We cover free and paid options for Mac & PC.",
    image: "/placeholder.png",
    date: "January 2, 2025",
    readTime: "10 min",
    category: "Podcast Software",
    author: {
      name: "Stephen Robles",
      role: "Video & Podcast Creator",
      avatar: "/placeholder.png",
    },
  },
  {
    id: "live-podcasting-guide",
    title: "A Guide To Live Podcasting | Audio & Video (2025)",
    description:
      "Discover the many benefits of live podcasting and find out how to record a live podcast with audio and video in the highest quality possible.",
    image: "/placeholder.png",
    date: "December 23, 2024",
    readTime: "15 min",
    category: "Podcast Software",
    author: {
      name: "Kendall Breitman",
      role: "Social Media & Community Expert",
      avatar: "/placeholder.png",
    },
  },
  {
    id: "start-podcast-guide",
    title: "How to Start a Podcast | Complete Step-by-Step Guide for 2025",
    description:
      "Learn how to start a podcast and podcast like a pro. The ultimate step-by-step guide on launching a podcast: from planning & equipment to publishing.",
    image: "/placeholder.png",
    date: "November 21, 2024",
    readTime: "22 min",
    category: "Start a podcast",
    author: {
      name: "Stephen Robles",
      role: "Video & Podcast Creator",
      avatar: "/placeholder.png",
    },
  },
  {
    id: "record-video-interviews",
    title: "4 Best Ways to Record Video Interviews Remotely Online",
    description:
      "Learn how to effortlessly record video interviews remotely. We cover 4 of the best methods and dive into tips for better remote video interviews.",
    image: "/placeholder.png",
    date: "May 7, 2024",
    readTime: "10 min",
    category: "Video recording",
    author: {
      name: "Stephen Robles",
      role: "Video & Podcast Creator",
      avatar: "/placeholder.png",
    },
  },
];

const BlogList = () => {
  const [posts, setPosts] = useState<BlogPost[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const fetchPosts = async () => {
      try {
        const { data } = await axios.get(
          "http://localhost:1337/api/blog-posts"
        );
        setPosts(data.data);
      } catch (error) {
        console.error("Error fetching posts:", error);
      } finally {
        setLoading(false);
      }
    };

    fetchPosts();
  }, []);

  console.log(posts);
  if (loading) return <p>Loading...</p>;

  // return (
  //   <div className="container mx-auto p-5">
  //     <h1 className="text-2xl font-bold">Blog</h1>
  //     {posts.map((post) => (
  //       <div key={post?.id} className="mt-4">
  //         <h2 className="text-xl font-semibold">{post?.title}</h2>
  //         <Link href={`/blog/${post?.slug}`} className="text-blue-500">
  //           Read More
  //         </Link>
  //       </div>
  //     ))}
  //   </div>
  // );

  return (
    <div className="container mx-auto mb-12 mt-8 px-4 py-8 max-w-7xl">
      {/* Featured Post */}
      {/* <div className="grid md:grid-cols-5 gap-8 mb-16"> */}
      <div className="flex flex-col md:flex-row  gap-8">
        <Link href={`/blog/${featuredPost.id}`} key={featuredPost.id}>
          <div className="flex flex-col md:flex-col gap-6 mb-16">
            <div className="md:col-span-3">
              <Image
                src={featuredPost.image || "/placeholder.png"}
                // src="/placeholder.png"
                alt={featuredPost.title}
                width={600}
                height={400}
                className="rounded-lg object-cover w-full h-[300px] md:h-[400px]"
              />
            </div>
            <div className="md:col-span-2 flex flex-col justify-center">
              <h1 className="text-3xl font-bold mb-4">{featuredPost.title}</h1>
              <p className="text-gray-600 mb-4">{featuredPost.description}</p>
              <div className="flex items-center gap-3 mt-2">
                <Avatar className="h-8 w-8">
                  <AvatarImage
                    src={featuredPost.author.avatar}
                    alt={featuredPost.author.name}
                  />
                  <AvatarFallback>
                    {featuredPost.author.name.charAt(0)}
                  </AvatarFallback>
                </Avatar>
                <div>
                  <p className="text-sm font-medium">
                    {featuredPost.author.name}
                  </p>
                  <p className="text-xs text-gray-500">
                    {featuredPost.author.role}
                  </p>
                </div>
                <div className="text-sm text-gray-500 ml-auto">
                  {featuredPost.date} • {featuredPost.readTime}
                </div>
              </div>
            </div>
          </div>
        </Link>

        {/* Trending Section */}
        <div className="mb-16">
          <div className="bg-purple-950 rounded-lg p-6 mb-6 min-h-[196px]">
            <h2 className="text-2xl font-bold text-white">
              Trending on TryKatch
            </h2>
          </div>
          <div className="space-y-6">
            {trendingPosts.map((post) => (
              <Link href={`/blog/${post.id}`} key={post.id}>
                <div className="grid grid-cols-4 gap-4 hover:bg-gray-50 p-2 rounded-lg transition-colors">
                  <div className="col-span-1">
                    <Image
                      src={post.image || "/placeholder.svg"}
                      alt={post.title}
                      width={180}
                      height={120}
                      className="rounded-lg object-cover w-full h-24"
                    />
                  </div>
                  <div className="col-span-3">
                    <h3 className="font-bold mb-2">{post.title}</h3>
                    <div className="flex items-center text-sm text-gray-500 mb-2">
                      <span>{post.date}</span>
                      <span className="mx-2">•</span>
                      <span>{post.readTime}</span>
                    </div>
                    <Badge
                      variant="outline"
                      className="bg-purple-100 text-purple-800 hover:bg-purple-200"
                    >
                      {post.category}
                    </Badge>
                  </div>
                </div>
              </Link>
            ))}
          </div>
        </div>
      </div>

      {/* Most Popular Posts */}
      <div>
        <h2 className="text-2xl font-bold mb-6">Most popular posts</h2>
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-8">
          {popularPosts.slice(0, 2).map((post) => (
            <Link href={`/blog/${post.id}`} key={post.id} className="group">
              <div className="rounded-lg overflow-hidden mb-3">
                <Image
                  src={post.image || "/placeholder.svg"}
                  alt={post.title}
                  width={400}
                  height={250}
                  className="w-full h-48 object-cover group-hover:scale-105 transition-transform duration-300"
                />
              </div>
              <Badge variant="outline" className="mb-2">
                {post.category}
              </Badge>
              <h3 className="font-bold text-lg mb-2 group-hover:text-purple-800 transition-colors">
                {post.title}
              </h3>
              <p className="text-gray-600 text-sm mb-3 line-clamp-2">
                {post.description}
              </p>
              <div className="flex items-center gap-3">
                <Avatar className="h-8 w-8">
                  <AvatarImage
                    src={post.author.avatar}
                    alt={post.author.name}
                  />
                  <AvatarFallback>{post.author.name.charAt(0)}</AvatarFallback>
                </Avatar>
                <div className="flex-1">
                  <p className="text-sm font-medium">{post.author.name}</p>
                  <p className="text-xs text-gray-500">{post.author.role}</p>
                </div>
                <div className="text-sm text-gray-500">
                  {post.date} • {post.readTime}
                </div>
              </div>
            </Link>
          ))}

          {/* Newsletter Subscription */}
          <div className="bg-purple-950 rounded-lg p-6 flex flex-col justify-center">
            <div className="relative h-full flex flex-col justify-between">
              <div>
                <h3 className="text-2xl font-bold text-white mb-2">
                  Never miss another article
                </h3>
              </div>
              <div className="mt-auto">
                <div className="relative mt-4">
                  <Input
                    type="email"
                    placeholder="Enter your email"
                    className="bg-white/10 text-white placeholder:text-gray-300 border-none"
                  />
                  <Button className="absolute right-0 top-0 bg-white text-purple-950 hover:bg-gray-100">
                    Subscribe
                  </Button>
                </div>
              </div>
              <div className="absolute -right-4 -top-4">
                <div className="bg-yellow-300 h-16 w-16 rounded-full flex items-center justify-center rotate-12">
                  <svg
                    xmlns="http://www.w3.org/2000/svg"
                    width="24"
                    height="24"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    className="text-purple-950"
                  >
                    <path d="M21.5 12H16c-.7 2-2 3-4 3s-3.3-1-4-3H2.5" />
                    <path d="M5.5 5.1L2 12v6c0 1.1.9 2 2 2h16a2 2 0 0 0 2-2v-6l-3.4-6.9A2 2 0 0 0 16.8 4H7.2a2 2 0 0 0-1.8 1.1z" />
                  </svg>
                </div>
              </div>
            </div>
          </div>

          {/* Remaining Popular Posts */}
          {popularPosts.slice(2).map((post) => (
            <Link href={`/blog/${post.id}`} key={post.id} className="group">
              <div className="rounded-lg overflow-hidden mb-3">
                <Image
                  src={post.image || "/placeholder.svg"}
                  alt={post.title}
                  width={400}
                  height={250}
                  className="w-full h-48 object-cover group-hover:scale-105 transition-transform duration-300"
                />
              </div>
              <Badge variant="outline" className="mb-2">
                {post.category}
              </Badge>
              <h3 className="font-bold text-lg mb-2 group-hover:text-purple-800 transition-colors">
                {post.title}
              </h3>
              <p className="text-gray-600 text-sm mb-3 line-clamp-2">
                {post.description}
              </p>
              <div className="flex items-center gap-3">
                <Avatar className="h-8 w-8">
                  <AvatarImage
                    src={post.author.avatar}
                    alt={post.author.name}
                  />
                  <AvatarFallback>{post.author.name.charAt(0)}</AvatarFallback>
                </Avatar>
                <div className="flex-1">
                  <p className="text-sm font-medium">{post.author.name}</p>
                  <p className="text-xs text-gray-500">{post.author.role}</p>
                </div>
                <div className="text-sm text-gray-500">
                  {post.date} • {post.readTime}
                </div>
              </div>
            </Link>
          ))}
        </div>
        <div className="text-center mt-8">
          <Button variant="outline">View More</Button>
        </div>
      </div>
    </div>
  );
};

export default BlogList;
